namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>外部授权换取已审核用户或一次性本地注册凭据。</summary>
public sealed record OAuthLoginResult(UserProfile? User, string? RegistrationTicket, string Email, string DisplayName);
