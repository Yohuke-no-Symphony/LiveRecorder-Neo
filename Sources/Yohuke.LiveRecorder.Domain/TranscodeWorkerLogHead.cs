namespace Yohuke.LiveRecorder.Domain;

/// <summary>保存节点已提交日志游标及可在超时或重启后恢复的单个发布意图。</summary>
public sealed class TranscodeWorkerLogHead : Entity
{
    public long CommittedCursor { get; set; }
    public string? PendingBatchId { get; set; }
    public long PendingCursor { get; set; }
}
