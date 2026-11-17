namespace Yohuke.LiveRecorder.Domain;

/// <summary>将一个独立物理采集及其执行归属固定映射到可见逻辑场次。</summary>
public sealed class RecordingCaptureBinding : Entity
{
    public string LogicalSessionId { get; set; } = "";
    public string? AssignmentId { get; set; }
    public int Generation { get; set; }
    public string DirectoryPath { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public bool IsClosed { get; set; }
    public bool IsLocal { get; set; }
    public bool HasDanmu { get; set; }
    public DateTime? LastMediaReceivedAtUtc { get; set; }
    public long LifecycleRevision { get; set; }
}
