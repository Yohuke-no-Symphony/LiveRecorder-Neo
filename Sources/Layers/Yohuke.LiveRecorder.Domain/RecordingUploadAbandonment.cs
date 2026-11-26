namespace Yohuke.LiveRecorder.Domain;

/// <summary>记录管理员对一个已结束物理场次的补传处置授权。</summary>
public sealed class RecordingUploadAbandonment : Entity
{
    public string WorkerId { get; set; } = "";
    public string AssignmentId { get; set; } = "";
    public int Generation { get; set; }
    public string RequestedByUserId { get; set; } = "";
    public DateTime? AcknowledgedAtUtc { get; set; }
}
