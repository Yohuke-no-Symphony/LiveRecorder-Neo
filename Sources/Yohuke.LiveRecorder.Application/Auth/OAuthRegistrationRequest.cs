namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>外部登录后的本地注册确认。</summary>
public sealed record OAuthRegistrationRequest(string Email, string DisplayName, string EmailCode, string InvitationCode);
