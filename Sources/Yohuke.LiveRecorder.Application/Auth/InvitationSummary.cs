namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>不包含保存摘要的邀请码状态。</summary>
public sealed record InvitationSummary(string Id, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc, int MaxUses, int UsedCount, bool Revoked, string? Code = null);
