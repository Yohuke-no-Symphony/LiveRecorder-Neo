namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>重发期间内容保持一致的节点日志批次。</summary>
public sealed record WorkerLogBatch
{
    public string StreamId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public long DroppedCount { get; set; }
    public List<WorkerLogEntry> Entries { get; set; } = [];
}
