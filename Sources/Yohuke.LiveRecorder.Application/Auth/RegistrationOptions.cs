namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>允许匿名读取的注册要求，不包含邮件或外部登录凭据。</summary>
public sealed record RegistrationOptions(bool Enabled, bool ApprovalRequired, bool InvitationRequired, bool EmailVerificationRequired);
