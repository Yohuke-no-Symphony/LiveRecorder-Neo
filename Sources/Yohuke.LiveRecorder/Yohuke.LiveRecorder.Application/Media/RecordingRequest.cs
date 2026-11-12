namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>一次连接的录制参数。</summary>
public sealed class RecordingRequest
{
    public string SessionId { get; set; } = "";
    public string AttemptId { get; set; } = Guid.NewGuid().ToString("N");
    public long RoomId { get; set; }
    public string StreamUrl { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string Title { get; set; } = "";
    public long MaxSegmentBytes { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime SessionStartedAtUtc { get; set; }
    public string FileTimeZoneId { get; set; } = "UTC";
    public Dictionary<string, string> Headers { get; set; } = new();
}
