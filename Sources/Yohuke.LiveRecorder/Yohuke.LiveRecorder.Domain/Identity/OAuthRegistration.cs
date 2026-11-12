namespace Yohuke.LiveRecorder.Domain;

/// <summary>已验证外部身份的短期注册凭据，不自动授予访问权。</summary>
public sealed class OAuthRegistration : Entity
{
    public string ProviderId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
