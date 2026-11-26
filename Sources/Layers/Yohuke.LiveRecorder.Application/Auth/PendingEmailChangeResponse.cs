namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>供账号设置继续验证或重发邮件的换绑状态。</summary>
public sealed record PendingEmailChangeResponse(string Email, DateTime ExpiresAtUtc, DateTime NextSendAtUtc);
