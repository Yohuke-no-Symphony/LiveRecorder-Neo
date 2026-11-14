namespace Yohuke.LiveRecorder.Media;

/// <summary>
/// Configures external media tools and their independent process limits.
/// </summary>
public sealed class MediaToolOptions
{
    public int ProbeConcurrency { get; set; } = 4;
    public TimeSpan StreamConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan StreamReadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HlsSegmentDuration { get; set; } = TimeSpan.FromSeconds(6);
    public TimeSpan FrameExtractionTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
