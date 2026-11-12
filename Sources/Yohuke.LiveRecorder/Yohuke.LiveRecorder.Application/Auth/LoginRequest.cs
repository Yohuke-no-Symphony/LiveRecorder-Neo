namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>本地账号密码登录参数。</summary>
public sealed record LoginRequest(string Email, string Password);
