namespace Yohuke.LiveRecorder.Infrastructure.Storage;

internal sealed class StorageUsageScanState
{
    public bool IsPartial { get; set; }
    public long RecordingBytes { get; set; }
    public long ClipBytes { get; set; }
    public long DerivedBytes { get; set; }
    public long TrashBytes { get; set; }
    public long CacheBytes { get; set; }
    public Dictionary<string, long> DerivedBytesByAssetId { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StorageRoomUsage> Rooms { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StorageUserUsage> Users { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StorageDirectoryUsage> Directories { get; } = new(StringComparer.Ordinal);
}
