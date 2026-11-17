namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>在新的物理采集或节点上继续同一逻辑场次所需的身份和等待状态。</summary>
public sealed record RecordingSessionContinuation
{
    public string LogicalSessionId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public RecordingSessionLifecycleState Lifecycle { get; set; } = new();
}
