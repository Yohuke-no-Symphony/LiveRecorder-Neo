namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>直播间实际目录及其回收文件的分类占用。</summary>
public sealed record StorageRoomUsage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long RoomId { get; set; }
    public bool RetainedInPlace { get; set; }
    public long RecordingBytes { get; set; }
    public long ClipBytes { get; set; }
    public long TrashBytes { get; set; }
    public long OtherBytes { get; set; }
    public long TotalBytes => RecordingBytes + ClipBytes + TrashBytes + OtherBytes;
}
