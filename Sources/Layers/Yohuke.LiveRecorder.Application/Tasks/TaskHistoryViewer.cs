namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>服务端认证身份及当前业务权限，不接受客户端指定身份。</summary>
public sealed record TaskHistoryViewer(string UserId, bool IsAdmin, IReadOnlySet<string> Permissions)
{
    /// <summary>管理员具备业务权限，其余用户使用当前权限集合。</summary>
    public bool Has(string permission) => IsAdmin || Permissions.Contains(permission);
}
