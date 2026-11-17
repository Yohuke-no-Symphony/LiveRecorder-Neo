namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>用户在多个场次目录中的个人切片及回收文件占用。</summary>
public sealed record StorageUserUsage
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long RecordingBytes { get; set; }
    public long ClipBytes { get; set; }
    public long TrashBytes { get; set; }
    public long OtherBytes { get; set; }
    public long TotalBytes => RecordingBytes + ClipBytes + TrashBytes + OtherBytes;
}
