namespace Yohuke.LiveRecorder.Domain;

/// <summary>记录管理员对特定录像进度版本的中断确认。</summary>
public sealed class RecordingTaskInterruption : Entity
{
    public string WorkerId { get; set; } = "";
    public string AssignmentId { get; set; } = "";
    public int Generation { get; set; }
    public DateTime LastReportedAtUtc { get; set; }
    public string InterruptedByUserId { get; set; } = "";
}
