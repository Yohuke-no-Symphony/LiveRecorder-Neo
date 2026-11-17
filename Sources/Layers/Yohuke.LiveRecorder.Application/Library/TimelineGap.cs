namespace Yohuke.LiveRecorder.Application.Library;

/// <summary>没有视频的真实时间范围。</summary>
public sealed class TimelineGap
{
    public double StartMs { get; set; }
    public double EndMs { get; set; }
}
