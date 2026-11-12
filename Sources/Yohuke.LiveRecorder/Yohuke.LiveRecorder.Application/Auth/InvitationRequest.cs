namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>创建限次邀请码。</summary>
public sealed record InvitationRequest(int MaxUses = 1, DateTime? ExpiresAtUtc = null);
