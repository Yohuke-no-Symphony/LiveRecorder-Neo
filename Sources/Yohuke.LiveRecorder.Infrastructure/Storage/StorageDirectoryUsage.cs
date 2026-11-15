namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>分类目录占用；已单独归入切片的子树不会再次计入录播目录行。</summary>
public sealed record StorageDirectoryUsage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "other";
    public long RecordingBytes { get; set; }
    public long ClipBytes { get; set; }
    public long DerivedBytes { get; set; }
    public long TrashBytes { get; set; }
    public long OtherBytes { get; set; }
    public long TotalBytes => RecordingBytes + ClipBytes + DerivedBytes + TrashBytes + OtherBytes;
}
