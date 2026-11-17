using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>以稳定批次身份原子保存节点日志，并在发布后分配连续行游标。</summary>
public sealed class TranscodeWorkerLogBatch : Entity
{
    public string WorkerId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public long CursorEnd { get; set; }
    public long DroppedCount { get; set; }
    public List<WorkerLogEntry> Entries { get; set; } = [];
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
}
