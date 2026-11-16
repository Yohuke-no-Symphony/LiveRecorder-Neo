namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>汇总所选缓存类别的清理结果，不将无法删除的文件计入释放空间。</summary>
public sealed class CacheCleanupResult
{
    public IReadOnlyList<CacheCleanupCategoryResult> Categories { get; init; } = [];
    public long DeletedFiles => Categories.Sum(category => category.DeletedFiles);
    public long DeletedBytes => Categories.Sum(category => category.DeletedBytes);
    public long SkippedEntries => Categories.Sum(category => category.SkippedEntries);
    public long FailedEntries => Categories.Sum(category => category.FailedEntries);
}
