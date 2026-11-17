using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>更新资源状态及当前任务的有效执行租约。</summary>
public sealed record WorkerHeartbeat
{
    public WorkerTelemetry Telemetry { get; set; } = new();
    public List<WorkerJobProgress> Jobs { get; set; } = [];
    public List<WorkerJobRecoveryRequest> RecoveryRequests { get; set; } = [];
    public List<RecordingRoomProgress> Recordings { get; set; } = [];
    public List<RecordingSessionProgress> RecordingSessions { get; set; } = [];
    public WorkerCapabilities? Capabilities { get; set; }
    public int AppliedConfigurationRevision { get; set; }
    public string? ConfigurationError { get; set; }
    public WorkerLogBatch? LogBatch { get; set; }
}
