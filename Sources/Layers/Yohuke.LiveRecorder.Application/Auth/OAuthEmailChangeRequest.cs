namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>指定通过已绑定的外部身份授权换绑的新邮箱。</summary>
public sealed record OAuthEmailChangeRequest(string Email);
