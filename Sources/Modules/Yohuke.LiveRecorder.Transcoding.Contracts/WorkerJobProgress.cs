namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>任务进度及执行身份。</summary>
public sealed record WorkerJobProgress
{
    public string JobId { get; set; } = "";
    public int Attempt { get; set; }
    public string LeaseToken { get; set; } = "";
    public string Stage { get; set; } = "Downloading";
    public double Progress { get; set; }
    public string? Encoder { get; set; }
    public WorkerJobActivity? Activity { get; set; }
    public List<WorkerStageSnapshot> Stages { get; set; } = [];
}
