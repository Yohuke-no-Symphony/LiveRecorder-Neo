namespace Yohuke.LiveRecorder.Domain.LiveMarkers;

/// <summary>独立保留实际时刻与直播内时长的直播记录。</summary>
public sealed record LiveMarkerEntry
{
    public DateTimeOffset RealDateTime { get; init; }
    public TimeSpan LiveTime { get; init; }
    public string Message { get; init; } = "";
    public LiveMarkerColor MarkerColor { get; init; }
}
