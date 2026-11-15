using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>可公开给浏览器的网站用户信息。</summary>
public sealed record UserProfile(string Id, string Email, string DisplayName, string AvatarUrl, string Role, string Status, string? Language = null)
{
    public PermissionSet Permissions { get; init; } = PermissionSet.Empty;
    public bool AnalyticsRangePinned { get; init; } = true;
    public int PlaybackEventRetentionCount { get; init; } = 500;
    [System.Text.Json.Serialization.JsonIgnore]
    public long SessionVersion { get; init; }
}
