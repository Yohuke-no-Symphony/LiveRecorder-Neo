namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Reports bounded room status while uploaded and locally captured progress remain distinct.</summary>
public sealed record RecordingRoomProgress
{
    public RecordingSessionContinuation? Continuation { get; init; }
    public string AssignmentId { get; init; } = "";
    public int Generation { get; init; }
    public string Token { get; init; } = "";
    public int ConfigurationRevision { get; init; }
    public string State { get; init; } = "Monitoring";
    public string? SessionId { get; init; }
    public long PendingUploadBytes { get; init; }
    public long SpoolBytes { get; init; }
    public DateTime? LastCapturedAtUtc { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public List<string> ErrorArguments { get; init; } = [];
    public RecordingRoomSnapshot? Room { get; init; }
}
