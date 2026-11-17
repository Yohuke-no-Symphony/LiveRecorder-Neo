namespace Yohuke.LiveRecorder.Domain;

/// <summary>Reserves a room to exactly one capture owner until an explicit drained stop releases it.</summary>
public sealed class RecordingOwnership : Entity
{
    public long RoomId { get; set; }
    public string TargetId { get; set; } = "";
    public string AssignmentId { get; set; } = "";
    public int Generation { get; set; }
    public string OwnerKind { get; set; } = "Local";
    public string OwnerId { get; set; } = "";
    public string State { get; set; } = "Released";
    public string? HandoffWorkerId { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}
