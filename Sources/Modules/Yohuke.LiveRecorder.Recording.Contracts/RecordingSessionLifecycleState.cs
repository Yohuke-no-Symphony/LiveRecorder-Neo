namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>可持久化并随采集所有权传递的逻辑场次事实。</summary>
public sealed record RecordingSessionLifecycleState
{
    public long Revision { get; set; }
    public DateTime? LastMediaReceivedAtUtc { get; set; }
    public DateTime? TimeoutAnchorUtc { get; set; }
    public DateTime? OfflineSinceUtc { get; set; }
    public DateTime? LastObservationAtUtc { get; set; }
    public bool? LastObservedIsLive { get; set; }
    public long LiveTime { get; set; }
    public RecordingSessionEndReason EndReason { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}
