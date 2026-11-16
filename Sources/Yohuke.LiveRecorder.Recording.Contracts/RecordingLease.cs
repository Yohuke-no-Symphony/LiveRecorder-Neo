namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Identifies durable room ownership independently from control connection availability.</summary>
public sealed record RecordingLease
{
    public string AssignmentId { get; init; } = "";
    public int Generation { get; init; }
    public string Token { get; init; } = "";
    public long RoomId { get; init; }
    public bool Enabled { get; init; } = true;
    public int ConfigurationRevision { get; init; } = 1;
}
