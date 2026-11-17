namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>本地注册资料，邮箱验证码和邀请码按服务器配置提供。</summary>
public sealed record RegisterRequest(string Email, string Password, string DisplayName, string? EmailCode = null, string? InvitationCode = null);
