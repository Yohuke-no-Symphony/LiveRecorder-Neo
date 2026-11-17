namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>外部登录后的本地注册确认，与普通注册使用相同的注册要求。</summary>
public sealed record OAuthRegistrationRequest(string Email, string DisplayName, string? EmailCode = null, string? InvitationCode = null);
