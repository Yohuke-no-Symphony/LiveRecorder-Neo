namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>切片管理表格所需的元数据和所有者显示名。</summary>
public sealed record ManagedClipResponse(
    string Id, string OwnerId, string SessionId, string Title, long Bytes, double StartMs, double EndMs,
    DateTime CreatedAtUtc, DateTime ExpiresAtUtc, string? OwnerDisplayName = null, string? OwnerAvatarUrl = null);
