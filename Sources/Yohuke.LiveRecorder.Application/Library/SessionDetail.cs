using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.Library;

/// <summary>场次及全场真实时间轴。</summary>
public sealed class SessionDetail
{
    public BroadcastSession Session { get; set; } = new();
    public List<RecordingSegment> Segments { get; set; } = [];
    public double DurationMs { get; set; }
    public List<TimelineGap> Gaps { get; set; } = [];
}
