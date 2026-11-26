namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>服务器验证已绑定 OAuth 身份后授予的邮箱换绑权限。</summary>
public sealed record OAuthEmailChangeAuthorization(string UserId, long SessionVersion, string Email);
