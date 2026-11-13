namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>与完整播放时间轴对齐的弹幕热度和互动记录。</summary>
public sealed class SessionTimelineResponse
{
    public double DurationMs { get; set; }
    public double BucketWidthMs { get; set; }
    public string Revision { get; set; } = "";
    public List<TimelineHeatBucket> Heat { get; set; } = [];
    public List<TimelineMarker> Markers { get; set; } = [];
}
