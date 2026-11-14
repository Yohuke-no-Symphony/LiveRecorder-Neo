using Yohuke.LiveRecorder.Application.Localization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>按目标窗口共享封装任务，并保护正在使用的缓存。</summary>
public sealed class PlaybackService(LibraryService library, PlaybackCache cache, IPlaybackWindowBuilder builder, IAppPaths paths, IOptions<PlaybackWindowOptions> options)
{
    private const double SOURCE_START_TOLERANCE_MS = 100;
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>定位媒体窗口或返回真实空档，不从头扫描到目标。</summary>
    public async Task<PlaybackResponse> OpenAsync(string sessionId, double offsetMs, CancellationToken cancellationToken = default)
    {
        return await OpenCoreAsync(sessionId, offsetMs, false, cancellationToken)
            ?? throw new InfrastructureException(MessageCode.PLAYBACK_WINDOW_NOT_READY, 425);
    }

    /// <summary>仅使用空闲生成容量准备后续窗口，忙碌或媒体未就绪时跳过。</summary>
    public Task<PlaybackResponse?> TryPrefetchAsync(string sessionId, double offsetMs, CancellationToken cancellationToken = default)
    {
        return OpenCoreAsync(sessionId, offsetMs, true, cancellationToken);
    }

    private async Task<PlaybackResponse?> OpenCoreAsync(string sessionId, double offsetMs, bool prefetch, CancellationToken cancellationToken)
    {
        var detail = await library.GetAsync(sessionId, cancellationToken);
        if (!double.IsFinite(offsetMs) || offsetMs < 0 || offsetMs > detail.DurationMs + 100)
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_TIME_OUT_OF_RANGE);
        }
        var gap = detail.Gaps.FirstOrDefault(x => offsetMs >= x.StartMs && offsetMs < x.EndMs);
        if (gap is not null)
        {
            return prefetch ? null : new PlaybackResponse { IsGap = true, RequestedOffsetMs = offsetMs, StartOffsetMs = gap.StartMs, EndOffsetMs = gap.EndMs, GapEndMs = gap.EndMs, DurationMs = detail.DurationMs };
        }
        var segment = detail.Segments.FirstOrDefault(x => offsetMs >= x.StartOffsetMs - 100 && offsetMs < x.StartOffsetMs + x.DurationMs);
        if (segment is null || segment.CommittedBytes < 13)
        {
            if (prefetch)
            {
                return null;
            }
            throw new InfrastructureException(MessageCode.PLAYBACK_MEDIA_NOT_COMMITTED, 425);
        }
        var windowDurationMs = options.Value.Duration.TotalMilliseconds;
        var windowStart = Math.Floor(Math.Max(0, offsetMs - segment.StartOffsetMs) / windowDurationMs) * windowDurationMs;
        var identity = FormattableString.Invariant($"{sessionId}:{segment.Id}:{segment.Revision}:avc-aac-v3:{windowDurationMs}:{windowStart}");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant();
        var slot = (int)(Convert.ToUInt32(key[..8], 16) % gates.Length);
        if (prefetch)
        {
            if (!await gates[slot].WaitAsync(0, cancellationToken))
            {
                return null;
            }
        }
        else
        {
            await gates[slot].WaitAsync(cancellationToken);
        }
        try
        {
            var cacheRoot = MediaPaths.Resolve(paths.CacheDirectory, Path.Combine("playback", key));
            var latestPath = Path.Combine(cacheRoot, "latest.json");
            PlaybackCacheEntry? entry = null;
            if (File.Exists(latestPath))
            {
                await using var file = File.OpenRead(latestPath);
                entry = await JsonSerializer.DeserializeAsync<PlaybackCacheEntry>(file, JsonFiles.Options, cancellationToken);
                if (entry is not null && (!Covers(entry, offsetMs)
                    || !File.Exists(Path.Combine(cacheRoot, entry.Generation, "index.m3u8"))
                    || !File.Exists(Path.Combine(cacheRoot, entry.Generation, "window.json"))))
                {
                    entry = null;
                }
            }
            if (entry is null)
            {
                var generation = Guid.NewGuid().ToString("N");
                var output = Path.Combine(cacheRoot, generation);
                var duration = Math.Min(windowDurationMs, segment.DurationMs - windowStart);
                if (duration < 200)
                {
                    if (prefetch)
                    {
                        return null;
                    }
                    throw new InfrastructureException(MessageCode.PLAYBACK_WINDOW_NOT_READY, 425);
                }
                var estimatedBytes = segment.CommittedBytes * (duration / Math.Max(1, segment.DurationMs)) * 2;
                if (!await cache.EnsureSpaceAsync(estimatedBytes, prefetch, cancellationToken))
                {
                    return null;
                }
                var request = new PlaybackWindowRequest
                {
                    SourcePath = MediaPaths.ResolveSegment(paths.RecordsDirectory, detail.Session.DirectoryPath, segment),
                    IndexPath = MediaPaths.ResolveSegmentIndex(paths.RecordsDirectory, detail.Session.DirectoryPath, segment),
                    SourceStartMs = windowStart,
                    DurationMs = duration,
                    CommittedBytes = segment.CommittedBytes,
                    IsClosed = segment.IsClosed,
                    Transcode = segment.Codec is not ("avc" or "h264"),
                    OutputDirectory = output
                };
                var published = false;
                using var protection = cache.ProtectBuild(key, generation);
                try
                {
                    var result = prefetch
                        ? await builder.TryPrefetchAsync(request, cancellationToken)
                        : await builder.BuildAsync(request, cancellationToken);
                    if (result is null)
                    {
                        return null;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    entry = new PlaybackCacheEntry
                    {
                        SessionId = sessionId,
                        SegmentId = segment.Id,
                        Generation = generation,
                        SourceBytes = segment.CommittedBytes,
                        StartOffsetMs = segment.StartOffsetMs + result.SourceStartMs,
                        EndOffsetMs = segment.StartOffsetMs + (result.ReachesCommittedEnd ? segment.DurationMs : Math.Min(result.SourceEndMs, segment.DurationMs)),
                        BuiltAtUtc = DateTime.UtcNow
                    };
                    if (!Covers(entry, offsetMs))
                    {
                        if (prefetch)
                        {
                            return null;
                        }
                        throw new InfrastructureException(MessageCode.PLAYBACK_WINDOW_NOT_READY, 425);
                    }
                    if (!File.Exists(Path.Combine(output, "index.m3u8")))
                    {
                        if (!File.Exists(result.PlaylistPath))
                        {
                            throw new InvalidDataException("播放清单生成失败。");
                        }
                        File.Copy(result.PlaylistPath, Path.Combine(output, "index.m3u8"));
                    }
                    await JsonFiles.WriteAsync(Path.Combine(output, "window.json"), entry, cancellationToken);
                    await JsonFiles.WriteAsync(latestPath, entry, cancellationToken);
                    published = true;
                }
                finally
                {
                    if (!published)
                    {
                        cache.Forget(key, generation);
                        if (Directory.Exists(output))
                        {
                            Directory.Delete(output, recursive: true);
                        }
                    }
                }
            }
            cache.Touch(key, entry.Generation);
            return new PlaybackResponse
            {
                Url = "/api/playback/" + key + "/" + entry.Generation + "/index.m3u8",
                StartOffsetMs = entry.StartOffsetMs,
                EndOffsetMs = entry.EndOffsetMs,
                RequestedOffsetMs = offsetMs,
                DurationMs = detail.DurationMs
            };
        }
        finally
        {
            gates[slot].Release();
        }
    }

    /// <summary>解析一个已生成的播放资源，并重新验证场次存在。</summary>
    public async Task<string> ResolveAssetAsync(string key, string generation, string fileName, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(key, "^[a-f0-9]{32}$") || !Regex.IsMatch(generation, "^[a-f0-9]{32}$") || fileName != Path.GetFileName(fileName) || fileName is "window.json" or "latest.json")
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_RESOURCE_NOT_FOUND, 404);
        }
        var directory = MediaPaths.Resolve(paths.CacheDirectory, Path.Combine("playback", key, generation));
        var manifest = Path.Combine(directory, "window.json");
        if (!File.Exists(manifest))
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_EXPIRED, 410);
        }
        await using var input = File.OpenRead(manifest);
        var entry = await JsonSerializer.DeserializeAsync<PlaybackCacheEntry>(input, JsonFiles.Options, cancellationToken)
                    ?? throw new InfrastructureException(MessageCode.PLAYBACK_RESOURCE_INVALID, 404);
        await library.GetAsync(entry.SessionId, cancellationToken);
        cache.Touch(key, generation);
        var path = MediaPaths.Resolve(directory, fileName);
        if (!File.Exists(path))
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_RESOURCE_NOT_FOUND, 404);
        }
        return path;
    }

    /// <summary>回收不活跃的过期窗口，并执行缓存容量限制。</summary>
    public Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        return cache.MaintainAsync(cancellationToken);
    }

    private static bool Covers(PlaybackCacheEntry entry, double offsetMs)
    {
        return double.IsFinite(entry.StartOffsetMs) && double.IsFinite(entry.EndOffsetMs)
            && entry.StartOffsetMs >= 0 && entry.EndOffsetMs > entry.StartOffsetMs
            && offsetMs >= entry.StartOffsetMs - SOURCE_START_TOLERANCE_MS && offsetMs < entry.EndOffsetMs;
    }
}
