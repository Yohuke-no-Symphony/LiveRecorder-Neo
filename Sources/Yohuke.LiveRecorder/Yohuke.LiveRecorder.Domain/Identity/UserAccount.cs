namespace Yohuke.LiveRecorder.Domain;

/// <summary>网站本地身份及人工审核状态。</summary>
public sealed class UserAccount : Entity
{
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Language { get; set; }
    public string AvatarUrl { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "User";
    public string Status { get; set; } = "Pending";
    public bool EmailVerified { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
