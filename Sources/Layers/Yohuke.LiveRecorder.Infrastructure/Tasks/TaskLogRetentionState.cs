using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

internal sealed class TaskLogRetentionState : Entity
{
    public int Days { get; set; } = 7;
    public long Revision { get; set; }
    public long AppliedRevision { get; set; } = -1;
    public bool AnchorsMigrated { get; set; }
    public string? ChangeId { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public DateTime SnapshotImportFloorUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
}
