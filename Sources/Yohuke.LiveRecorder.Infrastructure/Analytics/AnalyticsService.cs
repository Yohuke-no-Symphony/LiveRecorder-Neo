using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>按需从事件文件重建版本化观测统计，所有统计缓存仍为磁盘文件。</summary>
public sealed class AnalyticsService(EventArchiveReader reader)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>按来源区间投影统计，恢复裁切范围之前的金融去重基线。</summary>
    public Task<AnalyticsReport> GetAsync(BroadcastSession session, string storageRoot,
        CancellationToken cancellationToken = default)
    {
        return GetSessionAsync(session, storageRoot, null, cancellationToken);
    }

    /// <summary>按统一播放终点排除已结束场次的尾部互动，再计算金融去重和用户统计。</summary>
    public Task<AnalyticsReport> GetAsync(BroadcastSession session, string storageRoot, double playbackEndMs,
        CancellationToken cancellationToken = default)
    {
        return GetSessionAsync(session, storageRoot, playbackEndMs, cancellationToken);
    }

    private Task<AnalyticsReport> GetSessionAsync(BroadcastSession session, string storageRoot, double? playbackEndMs,
        CancellationToken cancellationToken)
    {
        var sources = SessionGroupingService.Sources(session, playbackEndMs).Select(source =>
            (Path: Path.Combine(MediaPaths.Resolve(storageRoot, source.DirectoryPath), "弹幕", "事件.jsonl"),
                Offset: source.StartOffsetMs, From: source.FromMs, To: source.ToMs)).ToList();
        var output = Path.Combine(MediaPaths.Resolve(storageRoot, session.DirectoryPath), "分析", "summary.json");
        return ReadAsync(sources, output, cancellationToken);
    }

    /// <summary>读取完整场次文件统计，文件增长时最多每三十秒重建一次。</summary>
    public Task<AnalyticsReport> GetAsync(string sessionDirectory, CancellationToken cancellationToken = default)
    {
        return ReadAsync([(Path.Combine(sessionDirectory, "弹幕", "事件.jsonl"), 0d, 0d, null)],
            Path.Combine(sessionDirectory, "分析", "summary.json"), cancellationToken);
    }

    private async Task<AnalyticsReport> ReadAsync(
        IReadOnlyList<(string Path, double Offset, double From, double? To)> sources, string output,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var identity = Hash(JsonSerializer.Serialize(
                sources.Select(source => new { source.Path, source.Offset, source.From, source.To }),
                JsonFiles.Options));
            var files = sources.Select(source => new FileInfo(source.Path)).ToArray();
            var length = files.Where(file => file.Exists).Sum(file => file.Length);
            var fingerprint = Hash(JsonSerializer.Serialize(files.Select(file => new
            {
                file.FullName, Length = file.Exists ? file.Length : 0,
                Modified = file.Exists ? file.LastWriteTimeUtc.Ticks : 0
            }), JsonFiles.Options));
            var saved = await ReadCacheAsync(output, cancellationToken);
            var now = DateTime.UtcNow;
            if (saved is not null && saved.SchemaVersion == AnalyticsReport.CURRENT_SCHEMA_VERSION &&
                AnalyticsCacheValidator.IsComplete(saved) && saved.SourceIdentity == identity
                && (saved.SourceFingerprint == fingerprint ||
                    saved.BuiltAtUtc <= now && now - saved.BuiltAtUtc < TimeSpan.FromSeconds(30)))
            {
                return saved;
            }

            var accumulator = new AnalyticsAccumulator();
            foreach (var source in sources)
            {
                if (source.From > 0)
                {
                    await foreach (var item in reader.EnumerateAsync(source.Path, cancellationToken))
                    {
                        if (item.OffsetMs < source.From)
                        {
                            accumulator.SeedBaseline(item);
                        }
                    }
                }

                await foreach (var item in reader.EnumerateAsync(source.Path, cancellationToken))
                {
                    if (item.OffsetMs < source.From || source.To is { } end && item.OffsetMs >= end)
                    {
                        continue;
                    }

                    item.OffsetMs += source.Offset;
                    accumulator.Add(item);
                }
            }

            var result = accumulator.Build();
            result.SourceBytes = length;
            result.SourceIdentity = identity;
            result.SourceFingerprint = fingerprint;
            await JsonFiles.WriteAsync(output, result, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<AnalyticsReport?> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AnalyticsReport>(stream, JsonFiles.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
