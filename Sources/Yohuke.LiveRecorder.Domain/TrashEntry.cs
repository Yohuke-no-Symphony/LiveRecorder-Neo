namespace Yohuke.LiveRecorder.Domain;

/// <summary>同卷可恢复删除的文件及原路径。</summary>
public sealed class TrashEntry : Entity
{
    public string OwnerId { get; set; } = "";
    public string Kind { get; set; } = "Clip";
    public string EntityId { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalRelativePath { get; set; } = "";
    public string TrashRelativePath { get; set; } = "";
    public bool RetainedInPlace { get; set; }
    public long Bytes { get; set; }
    public DateTime PurgeAtUtc { get; set; }
    public string State { get; set; } = "Pending";
    public string? OperationOwner { get; set; }
    public DateTime? OperationLeaseUntilUtc { get; set; }
}
