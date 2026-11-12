namespace Yohuke.LiveRecorder.Domain;

/// <summary>绑定浏览器、回调和 PKCE 的单次授权流程。</summary>
public sealed class OAuthFlow : Entity
{
    public string ProviderId { get; set; } = "";
    public string Intent { get; set; } = "Login";
    public string? LinkUserId { get; set; }
    public string BrowserBindingHash { get; set; } = "";
    public string ProtectedVerifier { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
