namespace Yohuke.LiveRecorder.Domain;

/// <summary>仅保存摘要并绑定用户会话版本的密码恢复授权。</summary>
public sealed class PasswordResetToken : Entity
{
    public string UserId { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public long IssuedSessionVersion { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
