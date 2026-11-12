using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>在归组与拆分后继续查询原始事件文件。</summary>
public sealed class SessionEventReader(EventArchiveReader reader)
{
    private const long SOURCE_SEQUENCE_STRIDE = 1_000_000_000_000;

    /// <summary>将来源文件序号投影为稳定的场次游标。</summary>
    public async Task<EventPage> ReadAsync(BroadcastSession session, string storageRoot, double fromMs, double toMs, string? kind, long? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (session.EventSources.Count == 0)
        {
            return await reader.ReadAsync(Path.Combine(MediaPaths.Resolve(storageRoot, session.DirectoryPath), "弹幕"), fromMs, toMs, kind, cursor, limit, cancellationToken);
        }
        limit = Math.Clamp(limit, 1, 1000);
        var items = new List<RecordedEventEnvelope>();
        var sources = SessionGroupingService.Sources(session);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var prefix = (index + 1) * SOURCE_SEQUENCE_STRIDE;
            if (cursor >= prefix + SOURCE_SEQUENCE_STRIDE)
            {
                continue;
            }
            var from = Math.Max(source.FromMs, fromMs - source.StartOffsetMs);
            var until = Math.Min(toMs - source.StartOffsetMs, source.ToMs is { } end ? Math.BitDecrement(end) : double.MaxValue);
            if (until < from)
            {
                continue;
            }
            var localCursor = cursor > prefix ? cursor - prefix : null;
            var page = await reader.ReadAsync(Path.Combine(MediaPaths.Resolve(storageRoot, source.DirectoryPath), "弹幕"), from, until, kind, localCursor, limit + 1 - items.Count, cancellationToken);
            foreach (var item in page.Items)
            {
                item.OffsetMs += source.StartOffsetMs;
                item.Sequence += prefix;
                items.Add(item);
            }
            if (items.Count > limit || page.NextCursor is not null)
            {
                return new EventPage { Items = items.Take(limit).ToList(), NextCursor = items[Math.Min(items.Count, limit) - 1].Sequence };
            }
        }
        return new EventPage { Items = items };
    }
}
