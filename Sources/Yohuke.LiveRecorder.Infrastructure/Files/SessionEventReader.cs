using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>在归组与拆分后继续查询原始事件文件。</summary>
public sealed class SessionEventReader(EventArchiveReader reader)
{
    internal const long SOURCE_SEQUENCE_STRIDE = 1_000_000_000_000;

    /// <summary>按播放时刻选择最近的展示消息，并预取后续消息及关联红包的原始阶段。</summary>
    public Task<PlaybackEventWindow> ReadPlaybackWindowAsync(SessionDetail detail, string storageRoot, double offsetMs, string? kind, int limit, CancellationToken cancellationToken = default)
    {
        return new PlaybackEventWindowReader(reader).ReadAsync(detail, storageRoot, offsetMs, kind, limit, cancellationToken);
    }

    /// <summary>将来源文件序号投影为稳定的场次游标。</summary>
    public Task<EventPage> ReadAsync(BroadcastSession session, string storageRoot, double fromMs, double toMs, string? kind, long? cursor, int limit, CancellationToken cancellationToken = default)
    {
        return ReadAsync(session, storageRoot, fromMs, toMs, kind, cursor, limit, null, cancellationToken);
    }

    /// <summary>筛选各来源的正文并投影为稳定的场次游标。</summary>
    public Task<EventPage> ReadAsync(BroadcastSession session, string storageRoot, double fromMs, double toMs, string? kind, long? cursor, int limit, Regex? textPattern, CancellationToken cancellationToken = default)
    {
        return ReadCoreAsync(session, storageRoot, fromMs, toMs, kind, cursor, limit, null, textPattern, cancellationToken);
    }

    /// <summary>按有效播放终点读取事件，在原始来源坐标上排除已结束场次的尾部。</summary>
    public Task<EventPage> ReadAsync(SessionDetail detail, string storageRoot, double fromMs, double toMs, string? kind, long? cursor, int limit, CancellationToken cancellationToken = default)
    {
        return ReadAsync(detail, storageRoot, fromMs, toMs, kind, cursor, limit, null, cancellationToken);
    }

    /// <summary>筛选正文并按有效播放终点排除各来源的尾部。</summary>
    public Task<EventPage> ReadAsync(SessionDetail detail, string storageRoot, double fromMs, double toMs, string? kind, long? cursor, int limit, Regex? textPattern, CancellationToken cancellationToken = default)
    {
        var playbackEnd = detail.Session.EndedAtUtc is null ? (double?)null : detail.DurationMs;
        return ReadCoreAsync(detail.Session, storageRoot, fromMs, toMs, kind, cursor, limit, playbackEnd, textPattern, cancellationToken);
    }

    private async Task<EventPage> ReadCoreAsync(BroadcastSession session, string storageRoot, double fromMs, double toMs, string? kind, long? cursor, int limit, double? playbackEndMs, Regex? textPattern, CancellationToken cancellationToken)
    {
        if (playbackEndMs is { } playbackEnd)
        {
            if (fromMs >= playbackEnd)
            {
                return new();
            }
            toMs = Math.Min(toMs, Math.BitDecrement(playbackEnd));
        }
        if (session.EventSources.Count == 0)
        {
            return await reader.ReadAsync(Path.Combine(MediaPaths.Resolve(storageRoot, session.DirectoryPath), "弹幕"), fromMs, toMs, kind, cursor, limit, textPattern, cancellationToken);
        }
        limit = Math.Clamp(limit, 1, 1000);
        var items = new List<RecordedEventEnvelope>();
        var sources = SessionGroupingService.Sources(session, playbackEndMs);
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
            var page = await reader.ReadAsync(Path.Combine(MediaPaths.Resolve(storageRoot, source.DirectoryPath), "弹幕"), from, until, kind, localCursor, limit + 1 - items.Count, textPattern, cancellationToken);
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
