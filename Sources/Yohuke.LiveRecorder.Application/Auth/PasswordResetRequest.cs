namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>请求向本站登记邮箱发送密码恢复链接。</summary>
public sealed record PasswordResetRequest(string Email);
