namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>新增录制目标。</summary>
public sealed record TargetRequest(long RoomId, string? AccountId = null, bool Enabled = true);
