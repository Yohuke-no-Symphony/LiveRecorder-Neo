using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>增量投影已提交事件，磁盘快照跨重启保存游标与去重基线。</summary>
public sealed class SessionTimelineService : IDisposable
{
    private const long SOURCE_SEQUENCE_STRIDE = 1_000_000_000_000;
    private const int CACHE_LOCK_SHARDS = 16;
    private static JsonSerializerOptions CacheJsonOptions { get; } = new(JsonFiles.Options) { RespectNullableAnnotations = true };
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, CACHE_LOCK_SHARDS).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>返回有界时间桶；直播追加只读取上次已提交字节之后的内容。</summary>
    public async Task<SessionTimelineResponse> GetAsync(BroadcastSession session, string storageRoot, double durationMs, int bucketCount = 300, CancellationToken cancellationToken = default)
    {
        var output = MediaPaths.Resolve(storageRoot, Path.Combine(session.DirectoryPath, "分析", "timeline.json"));
        var lockKey = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? output.ToUpperInvariant() : output;
        var gate = gates[(int)(Convert.ToUInt32(Hash(lockKey)[..8], 16) % gates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var sources = SessionGroupingService.Sources(session);
            var paths = sources.Select(source => MediaPaths.Resolve(storageRoot, Path.Combine(source.DirectoryPath, "弹幕", "事件.jsonl"))).ToArray();
            var identity = Hash(JsonSerializer.Serialize(sources.Select((source, index) => new { Path = paths[index], source.StartOffsetMs, source.FromMs, source.ToMs }), JsonFiles.Options));
            var saved = await ReadAsync(output, cancellationToken);
            var changed = saved is null || saved.SchemaVersion != TimelineProjectionCache.CURRENT_SCHEMA_VERSION || saved.SourceIdentity != identity || saved.Sources.Count != sources.Count;
            var cache = changed ? new TimelineProjectionCache { SourceIdentity = identity, Sources = sources.Select(_ => new TimelineSourceState()).ToList() } : saved!;
            for (var index = 0; index < sources.Count; index++)
            {
                var previous = cache.Sources[index];
                var token = (previous.Position, previous.ObservedLength, previous.LastWriteUtcTicks);
                var prefix = session.EventSources.Count == 0 ? 0 : (index + 1) * SOURCE_SEQUENCE_STRIDE;
                var updated = await TimelineArchiveReader.UpdateAsync(paths[index], sources[index], prefix, previous, cancellationToken);
                changed |= !ReferenceEquals(previous, updated) || token != (updated.Position, updated.ObservedLength, updated.LastWriteUtcTicks);
                cache.Sources[index] = updated;
            }
            if (changed)
            {
                await JsonFiles.WriteAsync(output, cache, cancellationToken);
            }
            return Project(cache, durationMs, bucketCount);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var gate in gates)
        {
            gate.Dispose();
        }
    }

    private static SessionTimelineResponse Project(TimelineProjectionCache cache, double durationMs, int bucketCount)
    {
        var duration = double.IsFinite(durationMs) ? Math.Max(0, durationMs) : 0;
        var count = Math.Clamp(bucketCount, 20, 1000);
        var width = Math.Max(TimelineProjectionAccumulator.HEAT_RESOLUTION_MS, Math.Ceiling(duration / count / TimelineProjectionAccumulator.HEAT_RESOLUTION_MS) * TimelineProjectionAccumulator.HEAT_RESOLUTION_MS);
        var heat = cache.Sources.SelectMany(source => source.Heat)
            .Where(pair => pair.Key * (double)TimelineProjectionAccumulator.HEAT_RESOLUTION_MS < duration)
            .GroupBy(pair => Math.Floor(pair.Key * TimelineProjectionAccumulator.HEAT_RESOLUTION_MS / width) * width)
            .OrderBy(group => group.Key)
            .Select(group => new TimelineHeatBucket { OffsetMs = group.Key, Count = group.Aggregate(0L, (sum, pair) => TimelineProjectionAccumulator.AddQuantity(sum, pair.Value)) }).ToList();
        var markers = cache.Sources.SelectMany(source => source.Markers.Values)
            .Where(marker => marker.Included && marker.OffsetMs < duration)
            .GroupBy(marker => (Bucket: Math.Floor(marker.OffsetMs / width), marker.Kind))
            .Select(group => Merge(group.OrderBy(marker => marker.OffsetMs).ThenBy(marker => marker.Sequence).ToList(), duration))
            .OrderBy(marker => marker.OffsetMs).ThenBy(marker => marker.Kind, StringComparer.Ordinal).ToList();
        var revision = Hash(JsonSerializer.Serialize(new { cache.SourceIdentity, Duration = duration, Width = width, Positions = cache.Sources.Select(source => new { source.Position, source.LastWriteUtcTicks }) }, JsonFiles.Options));
        return new() { DurationMs = duration, BucketWidthMs = width, Revision = revision, Heat = heat, Markers = markers };
    }

    private static TimelineMarker Merge(IReadOnlyList<TimelineMarkerState> records, double durationMs)
    {
        var first = records[0];
        decimal knownAmount = 0;
        var hasKnownAmount = false;
        var amountOverflowed = false;
        long quantity = 0;
        foreach (var record in records)
        {
            if (record.Amount is { } value)
            {
                hasKnownAmount = true;
                if (knownAmount > decimal.MaxValue - value)
                {
                    amountOverflowed = true;
                }
                else if (!amountOverflowed)
                {
                    knownAmount += value;
                }
            }
            quantity = TimelineProjectionAccumulator.AddQuantity(quantity, record.Quantity);
        }
        return new()
        {
            OffsetMs = first.OffsetMs, EndOffsetMs = Math.Min(durationMs, records.Max(record => record.EndOffsetMs)),
            Kind = first.Kind, Count = records.Count, Sequence = first.Sequence,
            UserName = first.UserName, Text = first.Text, Amount = hasKnownAmount && !amountOverflowed ? knownAmount : null, Quantity = quantity
        };
    }

    private static async Task<TimelineProjectionCache?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = File.OpenRead(path);
            var cache = await JsonSerializer.DeserializeAsync<TimelineProjectionCache>(input, CacheJsonOptions, cancellationToken);
            if (cache is null || ContainsNull(cache.Sources) || cache.Sources.Any(source => ContainsNull(source.Markers.Values)))
            {
                return null;
            }
            return cache;
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

    private static bool ContainsNull<T>(IEnumerable<T?> values) where T : class => values.Any(value => value is null);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
