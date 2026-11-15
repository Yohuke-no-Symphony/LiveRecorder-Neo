namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>心跳应答中的最新配置、续租与新任务。</summary>
public sealed record WorkerState
{
    public WorkerConfiguration Configuration { get; set; } = new();
    public int ConfigurationRevision { get; set; }
    public List<TranscodeAssignment> Assignments { get; set; } = [];
    public List<WorkerLeaseStatus> Leases { get; set; } = [];
}
