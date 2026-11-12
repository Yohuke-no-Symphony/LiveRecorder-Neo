namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>可公开给浏览器的网站用户信息。</summary>
public sealed record UserProfile(string Id, string Email, string DisplayName, string AvatarUrl, string Role, string Status);
