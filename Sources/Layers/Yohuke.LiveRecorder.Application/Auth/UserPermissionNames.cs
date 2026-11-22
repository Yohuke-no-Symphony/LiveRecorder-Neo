using System.Security.Claims;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>用户权限的授权策略名称及 Cookie 声明类型。</summary>
public static class UserPermissionNames
{
    public const string CLAIM_TYPE = "lr.permission";
    public const string VIEW_RECORDINGS = PermissionIds.VIEW_RECORDINGS;
    public const string DOWNLOAD_RECORDINGS = PermissionIds.DOWNLOAD_RECORDINGS;
    public const string CREATE_CLIPS = PermissionIds.CREATE_CLIPS;
    public const string MANAGE_CLIPS = PermissionIds.MANAGE_CLIPS;
    public const string UPLOAD_LIVE_MARKERS = PermissionIds.UPLOAD_LIVE_MARKERS;
    public const string MANAGE_LIVE_MARKERS = PermissionIds.MANAGE_LIVE_MARKERS;
    public const string GENERATE_LIVE_MARKERS = PermissionIds.GENERATE_LIVE_MARKERS;
    public const string MANAGE_RECORDINGS = PermissionIds.MANAGE_RECORDINGS;
    public const string CONFIGURE_RECORDINGS = PermissionIds.CONFIGURE_RECORDINGS;
    public const string VIEW_ANALYTICS = PermissionIds.VIEW_ANALYTICS;
    public const string GENERATE_ANALYTICS = PermissionIds.GENERATE_ANALYTICS;
    public const string PURGE_RECORDINGS = PermissionIds.PURGE_RECORDINGS;

    /// <summary>判断已认证账号是否拥有指定权限，管理员拥有全部权限。</summary>
    public static bool IsGranted(ClaimsPrincipal user, string permission) =>
        user.IsInRole("Admin") || user.HasClaim(CLAIM_TYPE, permission);
}
