namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>当前用户可使用的账号操作及正在进行的换绑验证。</summary>
public sealed record AccountSettingsResponse(bool HasLocalPassword, bool CanChangeEmail, PendingEmailChangeResponse? PendingEmailChange);
