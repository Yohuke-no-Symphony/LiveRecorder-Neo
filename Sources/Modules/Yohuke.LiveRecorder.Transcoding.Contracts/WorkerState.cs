using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>心跳应答中的最新配置、续租与新任务。</summary>
public sealed record WorkerState
{
    public WorkerConfiguration Configuration { get; set; } = new();
    public int ConfigurationRevision { get; set; }
    public List<TranscodeAssignment> Assignments { get; set; } = [];
    public List<ClipAssignment> ClipAssignments { get; set; } = [];
    public List<RecordingLease> RecordingAssignments { get; set; } = [];
    public List<AiAssignment> AiAssignments { get; set; } = [];
    public List<WorkerLeaseStatus> Leases { get; set; } = [];
    public List<WorkerJobRecoveryStatus> RecoveryResults { get; set; } = [];
    public WorkerLogAcknowledgement? LogAcknowledgement { get; set; }
}
