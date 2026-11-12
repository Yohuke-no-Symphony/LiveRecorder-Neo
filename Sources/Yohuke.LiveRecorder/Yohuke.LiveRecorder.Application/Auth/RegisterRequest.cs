namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>本地注册必须提供邮箱码与邀请码。</summary>
public sealed record RegisterRequest(string Email, string Password, string DisplayName, string EmailCode, string InvitationCode);
