namespace Yohuke.LiveRecorder.Domain;

/// <summary>与设置同次落盘、可在进程重启后补发的日志保留策略变更。</summary>
public sealed class TaskLogRetentionChange
{
    public string ChangeId { get; set; } = Guid.NewGuid().ToString("N");
    public int Days { get; set; }
    public long? ExpectedRevision { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
}
