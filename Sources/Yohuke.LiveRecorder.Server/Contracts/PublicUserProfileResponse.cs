using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>登录站点后可以查看的用户资料。</summary>
public sealed record PublicUserProfileResponse(string Id, string DisplayName, string AvatarUrl)
{
    internal static PublicUserProfileResponse? From(UserProfile? user) =>
        user is { Status: "Active" } ? new(user.Id, user.DisplayName, user.AvatarUrl) : null;
}
