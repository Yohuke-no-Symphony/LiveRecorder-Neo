namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>主站已持久化并发布的完整日志批次确认。</summary>
public sealed record WorkerLogAcknowledgement
{
    public string StreamId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public long ThroughSequence { get; set; }
}
