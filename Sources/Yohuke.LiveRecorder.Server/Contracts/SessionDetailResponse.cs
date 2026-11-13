using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>携带当前场次保留期限的播放详情响应。</summary>
public sealed class SessionDetailResponse(SessionDetail detail, DateTime? expiresAtUtc)
{
    public SessionResponse Session { get; } = new(detail.Session, expiresAtUtc);
    public List<RecordingSegment> Segments => detail.Segments;
    public double DurationMs => detail.DurationMs;
    public List<TimelineGap> Gaps => detail.Gaps;
}
