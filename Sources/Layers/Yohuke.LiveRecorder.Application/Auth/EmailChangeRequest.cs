namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>为新邮箱请求验证码或在原授权有效期内重发。</summary>
public sealed record EmailChangeRequest(string Email, string? CurrentPassword = null);
