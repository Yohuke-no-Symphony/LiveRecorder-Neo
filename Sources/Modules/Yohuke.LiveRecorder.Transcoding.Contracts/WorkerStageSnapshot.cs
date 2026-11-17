namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>有限任务中一个有序步骤的运行状态与累计耗时。</summary>
public sealed record WorkerStageSnapshot
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "Pending";
    public double Progress { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public double ElapsedMs { get; set; }
    public string? Substate { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public WorkerJobActivity? Activity { get; set; }
}
