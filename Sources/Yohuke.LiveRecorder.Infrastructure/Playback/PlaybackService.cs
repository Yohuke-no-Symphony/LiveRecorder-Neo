using Yohuke.LiveRecorder.Application.Localization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>按目标窗口共享封装任务，并保护正在使用的缓存。</summary>
public sealed class PlaybackService(LibraryService library, ISettingsStore settingsStore, IPlaybackWindowBuilder builder, IAppPaths paths)
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<string, DateTime> active = new();

    /// <summary>定位媒体窗口或返回真实空档，不从头扫描到目标。</summary>
    public async Task<PlaybackResponse> OpenAsync(string sessionId, double offsetMs, CancellationToken cancellationToken = default)
    {
        var detail = await library.GetAsync(sessionId, cancellationToken);
        if (!double.IsFinite(offsetMs) || offsetMs < 0 || offsetMs > detail.DurationMs + 100)
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_TIME_OUT_OF_RANGE);
        }
        var gap = detail.Gaps.FirstOrDefault(x => offsetMs >= x.StartMs && offsetMs < x.EndMs);
        if (gap is not null)
        {
            return new PlaybackResponse { IsGap = true, RequestedOffsetMs = offsetMs, StartOffsetMs = gap.StartMs, EndOffsetMs = gap.EndMs, GapEndMs = gap.EndMs, DurationMs = detail.DurationMs };
        }
        var segment = detail.Segments.FirstOrDefault(x => offsetMs >= x.StartOffsetMs - 100 && offsetMs < x.StartOffsetMs + x.DurationMs);
        if (segment is null || segment.CommittedBytes < 13)
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_MEDIA_NOT_COMMITTED, 425);
        }
        var settings = await settingsStore.GetAsync(cancellationToken);
        var windowStart = Math.Floor(Math.Max(0, offsetMs - segment.StartOffsetMs) / 120000) * 120000;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + ":" + segment.Id + ":" + segment.Revision + ":avc-aac-v1:" + windowStart)))[..32].ToLowerInvariant();
        var slot = (int)(Convert.ToUInt32(key[..8], 16) % gates.Length);
        await gates[slot].WaitAsync(cancellationToken);
        try
        {
            var cacheRoot = MediaPaths.Resolve(paths.CacheDirectory, Path.Combine("playback", key));
            var latestPath = Path.Combine(cacheRoot, "latest.json");
            PlaybackCacheEntry? entry = null;
            if (File.Exists(latestPath))
            {
                await using var file = File.OpenRead(latestPath);
                entry = await JsonSerializer.DeserializeAsync<PlaybackCacheEntry>(file, JsonFiles.Options, cancellationToken);
                if (entry is not null && (!File.Exists(Path.Combine(cacheRoot, entry.Generation, "index.m3u8")) || offsetMs >= entry.EndOffsetMs - 100))
                {
                    entry = null;
                }
            }
            if (entry is null)
            {
                var generation = Guid.NewGuid().ToString("N");
                var output = Path.Combine(cacheRoot, generation);
                var duration = Math.Min(120000, segment.DurationMs - windowStart);
                if (duration < 200)
                {
                    throw new InfrastructureException(MessageCode.PLAYBACK_WINDOW_NOT_READY, 425);
                }
                var estimatedBytes = segment.CommittedBytes * (duration / Math.Max(1, segment.DurationMs)) * 2;
                var freeBytes = new DriveInfo(paths.CacheDirectory).AvailableFreeSpace;
                if (freeBytes - estimatedBytes <= Storage.StorageService.MediaThreshold(settings))
                {
                    await MaintainAsync(cancellationToken);
                    freeBytes = new DriveInfo(paths.CacheDirectory).AvailableFreeSpace;
                    if (freeBytes - estimatedBytes <= Storage.StorageService.MediaThreshold(settings))
                    {
                        throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_SPACE_INSUFFICIENT, 507);
                    }
                }
                Touch(key + "/" + generation);
                var result = await builder.BuildAsync(new PlaybackWindowRequest
                {
                    SourcePath = MediaPaths.ResolveSegment(paths.RecordsDirectory, detail.Session.DirectoryPath, segment),
                    IndexPath = MediaPaths.ResolveSegmentIndex(paths.RecordsDirectory, detail.Session.DirectoryPath, segment),
                    SourceStartMs = windowStart,
                    DurationMs = duration,
                    CommittedBytes = segment.CommittedBytes,
                    IsClosed = segment.IsClosed,
                    Transcode = segment.Codec is not ("avc" or "h264"),
                    OutputDirectory = output
                }, cancellationToken);
                if (!File.Exists(Path.Combine(output, "index.m3u8")))
                {
                    if (!File.Exists(result.PlaylistPath))
                    {
                        throw new InvalidDataException("播放清单生成失败。");
                    }
                    File.Copy(result.PlaylistPath, Path.Combine(output, "index.m3u8"));
                }
                entry = new PlaybackCacheEntry
                {
                    SessionId = sessionId,
                    SegmentId = segment.Id,
                    Generation = generation,
                    SourceBytes = segment.CommittedBytes,
                    StartOffsetMs = segment.StartOffsetMs + result.SourceStartMs,
                    EndOffsetMs = segment.StartOffsetMs + result.SourceStartMs + result.DurationMs,
                    BuiltAtUtc = DateTime.UtcNow
                };
                await JsonFiles.WriteAsync(Path.Combine(output, "window.json"), entry, cancellationToken);
                await JsonFiles.WriteAsync(latestPath, entry, cancellationToken);
            }
            Touch(key + "/" + entry.Generation);
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
        Touch(key + "/" + generation);
        var path = MediaPaths.Resolve(directory, fileName);
        if (!File.Exists(path))
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_RESOURCE_NOT_FOUND, 404);
        }
        return path;
    }

    /// <summary>回收不活跃的过期窗口，并执行缓存容量限制。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var root = Path.Combine(paths.CacheDirectory, "playback");
        if (!Directory.Exists(root))
        {
            return;
        }
        var directories = Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories).Select(x => new DirectoryInfo(x)).OrderBy(x => x.LastWriteTimeUtc).ToList();
        var sizes = directories.ToDictionary(x => x.FullName, x => Storage.StorageService.DirectoryBytes(x.FullName));
        var total = sizes.Values.Sum();
        var deadline = DateTime.UtcNow.AddMinutes(-settings.CacheRetentionMinutes);
        foreach (var directory in directories)
        {
            var relative = Path.GetRelativePath(root, directory.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (active.TryGetValue(relative, out var access) && DateTime.UtcNow - access < TimeSpan.FromMinutes(5))
            {
                continue;
            }
            if (directory.LastWriteTimeUtc >= deadline && total <= settings.CacheLimitBytes)
            {
                continue;
            }
            Directory.Delete(directory.FullName, true);
            total -= sizes[directory.FullName];
            active.TryRemove(relative, out _);
        }
        foreach (var key in active.Where(x => DateTime.UtcNow - x.Value > TimeSpan.FromHours(2)).Select(x => x.Key).ToList())
        {
            active.TryRemove(key, out _);
        }
    }

    private void Touch(string key) => active[key] = DateTime.UtcNow;
}
