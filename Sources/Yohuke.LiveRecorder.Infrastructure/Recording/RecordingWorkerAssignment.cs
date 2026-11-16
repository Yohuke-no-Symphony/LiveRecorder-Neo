namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>用于管理界面展示的录像房间身份，不包含采集凭据。</summary>
public sealed record RecordingWorkerAssignment
{
    public string TargetId { get; init; } = "";
    public long RoomId { get; init; }
    public string Name { get; init; } = "";
}
