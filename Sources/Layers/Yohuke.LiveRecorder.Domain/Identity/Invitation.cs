namespace Yohuke.LiveRecorder.Domain;

/// <summary>带使用次数和过期时间的邀请码摘要。</summary>
public sealed class Invitation : Entity
{
    public string CodeHash { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime? ExpiresAtUtc { get; set; }
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public bool Revoked { get; set; }
}
