namespace Yohuke.LiveRecorder.Domain;

/// <summary>按邮箱摘要原子限制密码恢复邮件请求。</summary>
public sealed class PasswordResetThrottle : Entity
{
    public DateTime NextAllowedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
