namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>全场时间与当前 HLS 窗口的映射。</summary>
public sealed class PlaybackResponse
{
    public string? Url { get; set; }
    public double StartOffsetMs { get; set; }
    public double EndOffsetMs { get; set; }
    public double RequestedOffsetMs { get; set; }
    public double DurationMs { get; set; }
    public bool IsGap { get; set; }
    public double? GapEndMs { get; set; }
}
