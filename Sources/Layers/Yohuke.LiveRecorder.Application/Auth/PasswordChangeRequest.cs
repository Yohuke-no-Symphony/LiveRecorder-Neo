namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>通过当前本地密码授权设置新密码。</summary>
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
