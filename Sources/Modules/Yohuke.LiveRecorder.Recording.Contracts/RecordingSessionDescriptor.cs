namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Describes one stable broadcast session captured under a durable room assignment.</summary>
public sealed record RecordingSessionDescriptor
{
    public string SessionId { get; init; } = "";
    public string? LogicalSessionId { get; init; }
    public DateTime? LogicalStartedAtUtc { get; init; }
    public RecordingSessionLifecycleState? Lifecycle { get; init; }
    public long RoomId { get; init; }
    public long LiveTime { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public string Title { get; init; } = "";
    public string StreamerName { get; init; } = "";
    public long StreamerUid { get; init; }
    public string CoverUrl { get; init; } = "";
    public bool HasDanmu { get; init; }
    public bool IsClosed { get; init; }
    public DateTime? EndedAtUtc { get; init; }
}
