namespace Yohuke.LiveRecorder.Domain;

/// <summary>授权码和 PKCE 身份提供方的端点与字段映射。</summary>
public sealed class OAuthProviderSettings
{
    public string Id { get; set; } = "yohuke";
    public string Name { get; set; } = "Yohuke";
    public bool Enabled { get; set; }
    public string AuthorizationEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string UserInfoEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "profile email";
    public string SubjectField { get; set; } = "sub";
    public string NameField { get; set; } = "displayName";
    public string EmailField { get; set; } = "email";
    public string AvatarField { get; set; } = "avatarUrl";
    public bool UseBasicAuthentication { get; set; }
}
