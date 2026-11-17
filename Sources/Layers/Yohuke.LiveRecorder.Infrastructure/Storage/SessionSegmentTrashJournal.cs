using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>保存单个分段的回收意图和恢复所需的稳定文件引用。</summary>
public sealed class SessionSegmentTrashJournal
{
    public TrashEntry Entry { get; set; } = new();
    public RecordingSegment Segment { get; set; } = new();
    public string SessionDirectory { get; set; } = "";
    public int MediaRevision { get; set; }
    public string MediaRelativePath { get; set; } = "";
    public string? IndexRelativePath { get; set; }
}
