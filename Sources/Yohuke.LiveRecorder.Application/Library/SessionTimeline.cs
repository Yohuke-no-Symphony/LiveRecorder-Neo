using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.Library;

/// <summary>播放、弹幕和导出共用的真实时间轴运算。</summary>
public static class SessionTimeline
{
    /// <summary>构建覆盖范围与空档，已结束场次的播放范围截至最后有效媒体。</summary>
    public static SessionDetail Build(BroadcastSession session, IEnumerable<RecordingSegment> segments, DateTime nowUtc)
    {
        var ordered = segments.OrderBy(x => x.StartOffsetMs).ToList();
        var coverage = session.EndedAtUtc is null ? ordered : ordered.Where(segment => segment.DurationMs > 0).ToList();
        var duration = session.EndedAtUtc is null ? Math.Max(0, (nowUtc - session.StartedAtUtc).TotalMilliseconds) : 0;
        duration = Math.Max(duration, coverage.Select(segment => segment.StartOffsetMs + segment.DurationMs).DefaultIfEmpty(0).Max());
        var result = new SessionDetail { Session = session, Segments = ordered, DurationMs = duration };
        var cursor = 0d;
        foreach (var segment in coverage)
        {
            if (segment.StartOffsetMs > cursor + 100)
            {
                result.Gaps.Add(new TimelineGap { StartMs = cursor, EndMs = segment.StartOffsetMs });
            }
            cursor = Math.Max(cursor, segment.StartOffsetMs + segment.DurationMs);
        }
        if (duration > cursor + 100 && (session.EndedAtUtc is not null || ordered.Count > 0 && ordered.All(x => x.IsClosed)))
        {
            result.Gaps.Add(new TimelineGap { StartMs = cursor, EndMs = duration });
        }
        return result;
    }

    /// <summary>将导出区间解析为有序媒体与等时长空档。</summary>
    public static List<MediaInputSpan> ExportSpans(IEnumerable<RecordingSegment> segments, double startMs, double endMs, Func<string, string> resolvePath)
    {
        return ExportSegmentSpans(segments, startMs, endMs, segment => resolvePath(segment.RelativePath));
    }

    /// <summary>按片段的稳定存储引用解析媒体与真实空档。</summary>
    public static List<MediaInputSpan> ExportSegmentSpans(IEnumerable<RecordingSegment> segments, double startMs, double endMs, Func<RecordingSegment, string> resolvePath)
    {
        if (!double.IsFinite(startMs) || !double.IsFinite(endMs) || startMs < 0 || endMs <= startMs)
        {
            throw new InfrastructureException(MessageCode.TIMELINE_CLIP_RANGE_INVALID);
        }
        var result = new List<MediaInputSpan>();
        var cursor = startMs;
        foreach (var segment in segments.OrderBy(x => x.StartOffsetMs))
        {
            var from = Math.Max(cursor, segment.StartOffsetMs);
            var until = Math.Min(endMs, segment.StartOffsetMs + segment.DurationMs);
            if (until <= from)
            {
                continue;
            }
            if (from > cursor)
            {
                result.Add(new MediaInputSpan { SessionStartMs = cursor, DurationMs = from - cursor });
            }
            result.Add(new MediaInputSpan
            {
                Path = resolvePath(segment),
                SourceStartMs = from - segment.StartOffsetMs,
                SessionStartMs = from,
                DurationMs = until - from
            });
            cursor = until;
            if (cursor >= endMs)
            {
                break;
            }
        }
        if (cursor < endMs)
        {
            result.Add(new MediaInputSpan { SessionStartMs = cursor, DurationMs = endMs - cursor });
        }
        if (result.All(x => x.IsGap))
        {
            throw new InfrastructureException(MessageCode.TIMELINE_RECORDED_VIDEO_MISSING);
        }
        return result;
    }
}
