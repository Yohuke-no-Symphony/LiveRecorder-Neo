namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>提交新邮箱及其验证码以完成换绑。</summary>
public sealed record EmailChangeConfirmRequest(string Email, string Code);
