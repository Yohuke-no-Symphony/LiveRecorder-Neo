namespace Yohuke.LiveRecorder.Domain;

/// <summary>提供方稳定主体与网站账号的显式关联。</summary>
public sealed class ExternalLogin : Entity
{
    public string ProviderId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string UserId { get; set; } = "";
}
