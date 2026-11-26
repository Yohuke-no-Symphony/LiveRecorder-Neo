namespace Yohuke.LiveRecorder.Domain;

/// <summary>绑定指定邮箱和会话版本的一次性换绑验证。</summary>
public sealed class PendingEmailChange
{
    public string ChallengeId { get; set; } = "";
    public string Email { get; set; } = "";
    public long IssuedSessionVersion { get; set; }
    public string? CodeHash { get; set; }
    public string? Salt { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime NextSendAtUtc { get; set; }
    public int Attempts { get; set; }
}
