namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>固定时间范围内独立统计的弹幕数量。</summary>
public sealed class TimelineHeatBucket
{
    public double OffsetMs { get; set; }
    public long Count { get; set; }
}
