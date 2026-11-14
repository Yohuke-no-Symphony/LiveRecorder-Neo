namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>使用邮件中的单次授权设置本站密码。</summary>
public sealed record PasswordResetCompleteRequest(string Token, string NewPassword);
