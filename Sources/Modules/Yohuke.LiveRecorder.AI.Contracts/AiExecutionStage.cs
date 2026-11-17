namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>节点最终回传的执行步骤，不依赖其它 Worker 的任务协议。</summary>
public sealed record AiExecutionStage
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "Pending";
    public double Progress { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public double ElapsedMs { get; set; }
    public string? ErrorCode { get; set; }
}
