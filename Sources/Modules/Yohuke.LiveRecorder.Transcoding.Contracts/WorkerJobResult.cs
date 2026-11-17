namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>未生成成品的任务终态。</summary>
public sealed record WorkerJobResult
{
    public string State { get; set; } = "Failed";
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public List<WorkerStageSnapshot> Stages { get; set; } = [];
}
