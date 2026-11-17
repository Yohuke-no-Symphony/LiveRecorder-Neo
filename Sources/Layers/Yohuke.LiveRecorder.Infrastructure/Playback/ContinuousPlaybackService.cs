using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>为已完成录制提供稳定连续清单，并按请求生成可回收重建的独立分片。</summary>
public sealed class ContinuousPlaybackService : IDisposable
{
    private const string CACHE_GENERATION = "continuous-v1";
    private const int GATE_COUNT = 64;
    private readonly LibraryService library;
    private readonly IPlaybackFragmentSourceFactory factory;
    private readonly PlaybackCache cache;
    private readonly IAppPaths paths;
    private readonly ContinuousPlaybackOptions options;
    private readonly ILogger<ContinuousPlaybackService> logger;
    private readonly MemoryCache plans;
    private readonly SemaphoreSlim[] planGates = Enumerable.Range(0, GATE_COUNT).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SemaphoreSlim[] assetGates = Enumerable.Range(0, GATE_COUNT).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>共享媒体生成限制和磁盘缓存，为索引计划设置独立的内存上限。</summary>
    public ContinuousPlaybackService(LibraryService library, IPlaybackFragmentSourceFactory factory, PlaybackCache cache,
        IAppPaths paths, IOptions<ContinuousPlaybackOptions> options, ILogger<ContinuousPlaybackService> logger)
    {
        this.library = library;
        this.factory = factory;
        this.cache = cache;
        this.paths = paths;
        this.options = options.Value;
        this.logger = logger;
        plans = new(new MemoryCacheOptions { SizeLimit = this.options.PlanCacheLimitBytes });
    }

    /// <summary>只规划可连续播放的已关闭分段，其余媒体交由原窗口播放链路处理。</summary>
    public async Task<PlaybackResponse?> TryOpenAsync(string sessionId, double offsetMs, CancellationToken cancellationToken = default)
    {
        var detail = await library.GetAsync(sessionId, cancellationToken);
        if (!double.IsFinite(offsetMs) || offsetMs < 0 || offsetMs > detail.DurationMs + 100)
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_TIME_OUT_OF_RANGE);
        }
        if (detail.Gaps.Any(gap => offsetMs >= gap.StartMs && offsetMs < gap.EndMs))
        {
            return null;
        }
        var segment = detail.Segments.FirstOrDefault(segment => offsetMs >= segment.StartOffsetMs - 100
            && offsetMs < segment.StartOffsetMs + segment.DurationMs);
        if (segment is null || !SupportsContinuousPlayback(segment))
        {
            return null;
        }
        var identity = ReadIdentity(detail, segment);
        if (identity is null)
        {
            return null;
        }
        var source = await GetSourceAsync(identity, segment.CommittedBytes, cancellationToken);
        if (source is null)
        {
            return null;
        }
        var start = segment.StartOffsetMs + source.Fragments[0].SourceStartMs;
        var end = segment.StartOffsetMs + segment.DurationMs;
        if (offsetMs < start - 100 || offsetMs >= end)
        {
            return null;
        }
        return new()
        {
            Url = $"/api/sessions/{Uri.EscapeDataString(sessionId)}/playback/{Uri.EscapeDataString(segment.Id)}/{identity.Key}/index.m3u8",
            StartOffsetMs = start,
            EndOffsetMs = end,
            RequestedOffsetMs = offsetMs,
            DurationMs = detail.DurationMs
        };
    }

    /// <summary>验证当前录像版本后交付完整清单，生成清单不启动媒体进程。</summary>
    public async Task<ContinuousPlaybackAsset> ResolvePlaylistAsync(string sessionId, string segmentId, string sourceKey, CancellationToken cancellationToken = default)
    {
        var (identity, segment) = await ResolveSourceAsync(sessionId, segmentId, sourceKey, cancellationToken);
        var source = await GetSourceAsync(identity, segment.CommittedBytes, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_EXPIRED, 410);
        var key = Hash(sourceKey + ":manifest");
        var gate = Gate(assetGates, key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var protection = cache.ProtectBuild(key, CACHE_GENERATION);
            var directory = DirectoryPath(key);
            var playlist = Path.Combine(directory, "index.m3u8");
            if (!File.Exists(playlist))
            {
                var content = source.CreatePlaylist((sequence, fileName) => sequence.ToString(CultureInfo.InvariantCulture) + "/" + fileName);
                await cache.EnsureSpaceAsync(Encoding.UTF8.GetByteCount(content), false, cancellationToken);
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireUnchanged(identity);
                    File.Move(temporary, playlist, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
            var contentHash = await ContentHashAsync(playlist, cancellationToken);
            RequireUnchanged(identity);
            cache.Touch(key, CACHE_GENERATION);
            return new(playlist, contentHash);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>按清单序号生成或复用媒体片，每次请求重新校验场次和源版本。</summary>
    public async Task<ContinuousPlaybackAsset> ResolveFragmentAsync(string sessionId, string segmentId, string sourceKey, int sequence,
        string fileName, CancellationToken cancellationToken = default)
    {
        if (sequence < 0 || fileName is not ("init.mp4" or "chunk_00000.m4s"))
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_RESOURCE_NOT_FOUND, 404);
        }
        var (identity, segment) = await ResolveSourceAsync(sessionId, segmentId, sourceKey, cancellationToken);
        var source = await GetSourceAsync(identity, segment.CommittedBytes, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_EXPIRED, 410);
        if (sequence >= source.Fragments.Count)
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_RESOURCE_NOT_FOUND, 404);
        }
        var key = Hash(FormattableString.Invariant($"{sourceKey}:fragment:{sequence}"));
        var gate = Gate(assetGates, key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var protection = cache.ProtectBuild(key, CACHE_GENERATION);
            var directory = DirectoryPath(key);
            var marker = Path.Combine(directory, "fragment.json");
            ContinuousPlaybackFragmentMetadata? metadata;
            if (!File.Exists(marker) || !File.Exists(Path.Combine(directory, "init.mp4")) || !File.Exists(Path.Combine(directory, "chunk_00000.m4s")))
            {
                var fragment = source.Fragments[sequence];
                var estimatedBytes = source.CommittedBytes * (fragment.DurationMs / Math.Max(1, segment.DurationMs)) * 2;
                await cache.EnsureSpaceAsync(estimatedBytes, false, cancellationToken);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
                var published = false;
                try
                {
                    RequireUnchanged(identity);
                    await source.BuildFragmentAsync(sequence, directory, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireUnchanged(identity);
                    metadata = await CreateFragmentMetadataAsync(directory, sourceKey, sequence, cancellationToken);
                    RequireUnchanged(identity);
                    await JsonFiles.WriteAsync(marker, metadata, cancellationToken);
                    published = true;
                }
                finally
                {
                    if (!published)
                    {
                        cache.Forget(key, CACHE_GENERATION);
                        if (Directory.Exists(directory))
                        {
                            Directory.Delete(directory, recursive: true);
                        }
                    }
                }
            }
            else
            {
                metadata = await ReadFragmentMetadataAsync(marker, cancellationToken);
                if (metadata is null || metadata.SourceKey != sourceKey || metadata.Sequence != sequence
                    || !IsContentHash(metadata.InitializationContentHash) || !IsContentHash(metadata.MediaContentHash))
                {
                    metadata = await CreateFragmentMetadataAsync(directory, sourceKey, sequence, cancellationToken);
                    RequireUnchanged(identity);
                    await JsonFiles.WriteAsync(marker, metadata, cancellationToken);
                }
            }
            RequireUnchanged(identity);
            cache.Touch(key, CACHE_GENERATION);
            return new(Path.Combine(directory, fileName), fileName == "init.mp4" ? metadata.InitializationContentHash! : metadata.MediaContentHash!);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(ContinuousPlaybackIdentity Identity, RecordingSegment Segment)> ResolveSourceAsync(
        string sessionId, string segmentId, string sourceKey, CancellationToken cancellationToken)
    {
        if (sourceKey.Length != 64 || sourceKey.Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_RESOURCE_NOT_FOUND, 404);
        }
        var detail = await library.GetAsync(sessionId, cancellationToken);
        var segment = detail.Segments.FirstOrDefault(segment => segment.Id == segmentId);
        if (segment is null || !SupportsContinuousPlayback(segment))
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_EXPIRED, 410);
        }
        var identity = ReadIdentity(detail, segment);
        if (identity is null || identity.Key != sourceKey)
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_EXPIRED, 410);
        }
        return (identity, segment);
    }

    private async Task<IPlaybackFragmentSource?> GetSourceAsync(ContinuousPlaybackIdentity identity, long committedBytes, CancellationToken cancellationToken)
    {
        if (plans.TryGetValue<ContinuousPlaybackPlan>(identity.Key, out var existing))
        {
            return existing!.Source;
        }
        var gate = Gate(planGates, identity.Key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (plans.TryGetValue<ContinuousPlaybackPlan>(identity.Key, out existing))
            {
                return existing!.Source;
            }
            IPlaybackFragmentSource? source = null;
            try
            {
                source = await factory.OpenAsync(new()
                {
                    SourcePath = identity.SourcePath,
                    IndexPath = identity.IndexPath,
                    CommittedBytes = committedBytes,
                    IsClosed = true
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
            {
                logger.LogDebug(exception, "Recording {SourcePath} uses windowed playback because its index cannot form a continuous stream.", identity.SourcePath);
            }
            RequireUnchanged(identity);
            plans.Set(identity.Key, new ContinuousPlaybackPlan(source), new MemoryCacheEntryOptions
            {
                Size = 4096L + (source?.Fragments.Count ?? 0) * 512L,
                SlidingExpiration = options.PlanCacheSlidingExpiration,
                AbsoluteExpirationRelativeToNow = options.PlanCacheAbsoluteExpiration
            });
            return source;
        }
        finally
        {
            gate.Release();
        }
    }

    private ContinuousPlaybackIdentity? ReadIdentity(SessionDetail detail, RecordingSegment segment)
    {
        var sourcePath = MediaPaths.ResolveSegment(paths.RecordsDirectory, detail.Session.DirectoryPath, segment);
        var indexPath = MediaPaths.ResolveSegmentIndex(paths.RecordsDirectory, detail.Session.DirectoryPath, segment);
        var source = new FileInfo(sourcePath);
        var index = new FileInfo(indexPath);
        if (!source.Exists || !index.Exists)
        {
            return null;
        }
        var identity = System.Text.Json.JsonSerializer.Serialize(new
        {
            detail.Session.Id,
            detail.Session.MediaRevision,
            SegmentId = segment.Id,
            segment.Revision,
            segment.CommittedBytes,
            segment.StartOffsetMs,
            segment.DurationMs,
            sourcePath,
            indexPath,
            SourceLength = source.Length,
            SourceWriteTicks = source.LastWriteTimeUtc.Ticks,
            IndexLength = index.Length,
            IndexWriteTicks = index.LastWriteTimeUtc.Ticks,
            factory.CacheVersion
        });
        return new(Hash(identity), sourcePath, indexPath, source.Length, source.LastWriteTimeUtc.Ticks, index.Length, index.LastWriteTimeUtc.Ticks);
    }

    private string DirectoryPath(string key) => MediaPaths.Resolve(paths.CacheDirectory, Path.Combine("playback", key, CACHE_GENERATION));

    private static bool SupportsContinuousPlayback(RecordingSegment segment) => segment.IsClosed && segment.CommittedBytes >= 13
        && segment.DurationMs > 0 && segment.Codec is "avc" or "h264"
        && (!string.IsNullOrWhiteSpace(segment.RootIndexRelativePath) || !string.IsNullOrWhiteSpace(segment.IndexRelativePath));

    private static void RequireUnchanged(ContinuousPlaybackIdentity identity)
    {
        if (!identity.MatchesFiles())
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_EXPIRED, 410);
        }
    }

    private static SemaphoreSlim Gate(SemaphoreSlim[] gates, string key) => gates[(int)(Convert.ToUInt32(key[..8], 16) % gates.Length)];

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> ContentHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task<ContinuousPlaybackFragmentMetadata> CreateFragmentMetadataAsync(string directory, string sourceKey,
        int sequence, CancellationToken cancellationToken)
    {
        var initializationHash = await ContentHashAsync(Path.Combine(directory, "init.mp4"), cancellationToken);
        var mediaHash = await ContentHashAsync(Path.Combine(directory, "chunk_00000.m4s"), cancellationToken);
        return new(sourceKey, sequence, initializationHash, mediaHash);
    }

    private static async Task<ContinuousPlaybackFragmentMetadata?> ReadFragmentMetadataAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<ContinuousPlaybackFragmentMetadata>(stream, JsonFiles.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsContentHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    /// <summary>停止请求处理后释放索引计划和同步资源。</summary>
    public void Dispose()
    {
        plans.Dispose();
        foreach (var gate in planGates.Concat(assetGates))
        {
            gate.Dispose();
        }
    }
}
