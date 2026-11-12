namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>请求注册邮箱验证码。</summary>
public sealed record EmailCodeRequest(string Email, string Purpose = "register");
