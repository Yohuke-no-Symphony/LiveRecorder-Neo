namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>录播所在真实文件系统卷的容量。</summary>
public sealed class StorageVolume
{
    public string Id { get; set; } = "recordings";
    public string Path { get; set; } = "";
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long TrashBytes { get; set; }
    public long FreeBytes { get; set; }
    public long CacheBytes { get; set; }
}
