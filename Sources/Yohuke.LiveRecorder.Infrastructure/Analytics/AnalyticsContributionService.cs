using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>复用金融观测的业务去重及裁切基线，按需分页读取整场赠送明细。</summary>
public sealed class AnalyticsContributionService(EventArchiveReader reader, IAppPaths paths) : IDisposable
{
    private const long SOURCE_SEQUENCE_STRIDE = 1_000_000_000_000;
    private const long CACHE_LIMIT_BYTES = 32 * 1024 * 1024;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = CACHE_LIMIT_BYTES });
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AnalyticsContributionSnapshotStore snapshots = new(paths);

    /// <summary>返回完整选区的分类合计，以及业务明细或用户项目合计的一页。</summary>
    public async Task<AnalyticsContributionPage> GetAsync(SessionDetail detail, string storageRoot, string kind,
        string view = "events", double? fromMs = null, double? toMs = null, int cursor = 0, int limit = 50,
        string? snapshotId = null, CancellationToken cancellationToken = default)
    {
        if (kind is not ("SuperChat" or "Gift" or "Guard" or "RedPocket" or "All") || view is not ("events" or "users") || kind == "All" && view != "users" ||
            cursor < 0 || cursor > 0 && snapshotId is null ||
            snapshotId is not null && (snapshotId.Length != 64 || !snapshotId.All(char.IsAsciiHexDigit)))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        if (!double.IsFinite(detail.DurationMs) || detail.DurationMs < 0 ||
            fromMs is { } from && (!double.IsFinite(from) || from < 0 || from >= detail.DurationMs) ||
            toMs is { } until && (!double.IsFinite(until) || until <= (fromMs ?? 0) || until > detail.DurationMs))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_EVENT_RANGE_INVALID);
        }
        var rangeFrom = fromMs ?? 0;
        var rangeTo = toMs ?? detail.DurationMs;
        var sources = SessionGroupingService.Sources(detail.Session, detail.DurationMs);
        var files = sources.Select(source => new FileInfo(MediaPaths.Resolve(storageRoot,
            Path.Combine(source.DirectoryPath, "弹幕", "事件.jsonl")))).ToArray();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            detail.Session.Id,
            SchemaVersion = AnalyticsContributionSnapshot.CURRENT_SCHEMA_VERSION,
            Kind = kind,
            FromMs = rangeFrom,
            ToMs = rangeTo,
            Projected = detail.Session.EventSources.Count > 0,
            Sources = sources.Select((source, index) => new
            {
                files[index].FullName,
                source.StartOffsetMs,
                source.FromMs,
                source.ToMs,
                Length = files[index].Exists ? files[index].Length : 0,
                Modified = files[index].Exists ? files[index].LastWriteTimeUtc.Ticks : 0
            })
        }, JsonFiles.Options))));
        await gate.WaitAsync(cancellationToken);
        AnalyticsContributionSnapshot snapshot;
        try
        {
            var requestedIdentity = snapshotId ?? identity;
            cache.TryGetValue(requestedIdentity, out AnalyticsContributionSnapshot? saved);
            saved ??= await snapshots.ReadAsync(requestedIdentity, cancellationToken);
            if (saved is null)
            {
                if (snapshotId is not null && snapshotId != identity)
                {
                    throw new InfrastructureException(MessageCode.REQUEST_INVALID, 409);
                }
                var accumulator = new AnalyticsAccumulator(captureContributions: true);
                for (var index = 0; index < sources.Count; index++)
                {
                    var source = sources[index];
                    var sourceFrom = Math.Max(source.FromMs, Math.Max(0, rangeFrom - source.StartOffsetMs));
                    var sourceTo = Math.Min(source.ToMs ?? double.MaxValue, rangeTo - source.StartOffsetMs);
                    if (sourceFrom > 0)
                    {
                        await foreach (var item in reader.EnumerateAsync(files[index].FullName, cancellationToken))
                        {
                            if (IncludesKind(kind, item.Kind) && item.OffsetMs >= 0 && item.OffsetMs < Math.Min(sourceFrom, source.ToMs ?? double.MaxValue))
                            {
                                accumulator.SeedBaseline(item);
                            }
                        }
                    }
                    var prefix = detail.Session.EventSources.Count == 0 ? 0 : (index + 1) * SOURCE_SEQUENCE_STRIDE;
                    await foreach (var item in reader.EnumerateAsync(files[index].FullName, cancellationToken))
                    {
                        if (!IncludesKind(kind, item.Kind) || item.OffsetMs < sourceFrom || item.OffsetMs >= sourceTo)
                        {
                            continue;
                        }
                        var offset = item.OffsetMs + source.StartOffsetMs;
                        if (offset < rangeFrom || offset >= rangeTo)
                        {
                            continue;
                        }
                        item.OffsetMs = offset;
                        item.Sequence = checked(prefix + item.Sequence);
                        accumulator.Add(item);
                    }
                }
                saved = AnalyticsContributionBuilder.Build(accumulator, kind);
                saved.SessionId = detail.Session.Id;
                saved.StorageRoot = storageRoot;
                saved.Kind = kind;
                saved.FromMs = rangeFrom;
                saved.ToMs = rangeTo;
                await snapshots.WriteAsync(identity, saved, cancellationToken);
            }
            snapshot = saved;
            if (snapshot.SessionId != detail.Session.Id || snapshot.StorageRoot != storageRoot || snapshot.Kind != kind ||
                !snapshot.FromMs.Equals(rangeFrom) || toMs.HasValue && !snapshot.ToMs.Equals(rangeTo))
            {
                throw new InfrastructureException(MessageCode.REQUEST_INVALID, 409);
            }
            identity = requestedIdentity;
            var bytes = EstimateSize(snapshot);
            if (bytes <= CACHE_LIMIT_BYTES)
            {
                cache.Set(identity, snapshot, new MemoryCacheEntryOptions
                {
                    Size = bytes,
                    SlidingExpiration = TimeSpan.FromMinutes(2),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
                });
            }
        }
        finally
        {
            gate.Release();
        }
        cancellationToken.ThrowIfCancellationRequested();
        var rows = view == "users" ? snapshot.Users : snapshot.Events;
        var items = rows.Skip(cursor).Take(Math.Clamp(limit, 1, 200)).ToList();
        var next = cursor + items.Count;
        return new()
        {
            Kind = kind,
            View = view,
            SnapshotId = identity,
            FromMs = snapshot.FromMs,
            ToMs = snapshot.ToMs,
            Items = items,
            TotalCount = rows.Count,
            NextCursor = next < rows.Count ? next : null,
            Summary = snapshot.Summary
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        cache.Dispose();
        gate.Dispose();
    }

    private static long EstimateSize(AnalyticsContributionSnapshot snapshot) => 256 +
        snapshot.Events.Concat(snapshot.Users).Sum(item => 512L + 2L *
            (item.Id.Length + item.UserId.Length + item.UserName.Length + item.Text.Length + item.GiftId.Length + item.GiftName.Length +
             (item.AvatarUrl?.Length ?? 0) + (item.Event?.Payload?.ToJsonString().Length ?? 0) + JsonSerializer.Serialize(item.Categories, JsonFiles.Options).Length));

    private static bool IncludesKind(string requestedKind, string eventKind) =>
        requestedKind == eventKind || requestedKind == "All" && eventKind is "SuperChat" or "Gift" or "Guard" or "RedPocket";
}
