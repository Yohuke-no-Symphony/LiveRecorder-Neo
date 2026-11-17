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
    public string RequestedQualityId { get; set; } = "original";
    public string EffectiveQualityId { get; set; } = "original";
    public string? ArtifactVersion { get; set; }
    public string? TimestampMapping { get; set; }
    public double? MediaStartOffsetMs { get; set; }
    public double? MediaEndTimestampMs { get; set; }
    public string? FallbackReason { get; set; }
    public IReadOnlyList<PlaybackQuality> Qualities { get; set; } = [new("original", "原画", true, null, "Ready")];
}
