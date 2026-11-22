namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>同一次扫描得到的卷容量与分类目录占用快照。</summary>
public sealed record StorageUsageReport
{
    public DateTime ScannedAtUtc { get; init; }
    public bool IsPartial { get; init; }
    public IReadOnlyList<StorageVolume> Volumes { get; init; } = [];
    public IReadOnlyList<StorageRoomUsage> Rooms { get; init; } = [];
    public IReadOnlyList<StorageUserUsage> Users { get; init; } = [];
    public IReadOnlyList<StorageDirectoryUsage> Directories { get; init; } = [];
    internal IReadOnlyDictionary<string, long> DerivedBytesByAssetId { get; init; } = new Dictionary<string, long>();
}
