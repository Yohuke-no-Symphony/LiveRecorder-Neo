namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Contains the durable room configuration needed to continue capturing while the server is unavailable.</summary>
public sealed record RecordingAssignment
{
    public string AssignmentId { get; init; } = "";
    public int Generation { get; init; }
    public string Token { get; init; } = "";
    public string WorkerId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public long RoomId { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int ConfigurationRevision { get; init; } = 1;
    public int Quality { get; init; } = 10000;
    public long SegmentBytes { get; init; }
    public bool RecordDanmu { get; init; } = true;
    public int SessionGraceMinutes { get; init; } = 10;
    public int RecordingTimeoutMinutes { get; init; } = 5;
    public int MonitorIntervalSeconds { get; init; } = 30;
    public string TimeZone { get; init; } = "Asia/Shanghai";
    public RecordingSessionContinuation? Continuation { get; init; }
    public List<RecordingAccount> Accounts { get; init; } = [];
}
