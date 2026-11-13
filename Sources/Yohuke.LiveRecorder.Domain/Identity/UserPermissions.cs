namespace Yohuke.LiveRecorder.Domain;

/// <summary>管理员可独立授予用户的录像管理和分析权限快照。</summary>
public sealed record UserPermissions
{
    public bool ManageRecordings { get; init; }
    public bool ViewAnalytics { get; init; }
    public bool PurgeRecordings { get; init; }
}
