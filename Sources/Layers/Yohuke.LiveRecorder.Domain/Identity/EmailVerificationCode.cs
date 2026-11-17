namespace Yohuke.LiveRecorder.Domain;

/// <summary>仅保存摘要的单次邮箱验证码。</summary>
public sealed class EmailVerificationCode : Entity
{
    public string Email { get; set; } = "";
    public string Purpose { get; set; } = "register";
    public string CodeHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public bool Consumed { get; set; }
}
