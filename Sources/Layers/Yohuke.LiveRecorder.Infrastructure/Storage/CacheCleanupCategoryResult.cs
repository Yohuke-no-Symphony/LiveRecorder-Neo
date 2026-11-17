namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>单类缓存清理中实际删除的文件、逻辑字节和未处理条目。</summary>
public sealed class CacheCleanupCategoryResult
{
    public required string Category { get; init; }
    public long DeletedFiles { get; internal set; }
    public long DeletedBytes { get; internal set; }
    public long SkippedEntries { get; internal set; }
    public long FailedEntries { get; internal set; }
}
