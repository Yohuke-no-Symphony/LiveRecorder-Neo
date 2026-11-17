using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>按当前回收站保留设置计算删除期限，零天表示关闭自动彻底删除。</summary>
public static class TrashRetentionPolicy
{
    /// <summary>以条目移入回收站的时间计算当前期限。</summary>
    public static DateTime? GetPurgeAtUtc(TrashEntry entry, ServerSettings settings)
    {
        return settings.TrashRetentionDays > 0
            ? entry.CreatedAtUtc.AddDays(settings.TrashRetentionDays)
            : null;
    }
}
