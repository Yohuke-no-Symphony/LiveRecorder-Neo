namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>用户权限的授权策略名称及 Cookie 声明类型。</summary>
public static class UserPermissionNames
{
    public const string CLAIM_TYPE = "lr.permission";
    public const string MANAGE_RECORDINGS = "recordings.manage";
    public const string VIEW_ANALYTICS = "analytics.view";
    public const string PURGE_RECORDINGS = "recordings.purge";
}
