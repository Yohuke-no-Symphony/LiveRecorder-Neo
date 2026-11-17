namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>网页可按稳定行游标增量读取的节点运行日志。</summary>
public sealed record TranscodeWorkerLogEntry
{
    public string Id { get; init; } = "";
    public string WorkerId { get; init; } = "";
    public string StreamId { get; init; } = "";
    public long Sequence { get; init; }
    public long Cursor { get; init; }
    public DateTime TimestampUtc { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
    public string Level { get; init; } = "Information";
    public string Message { get; init; } = "";
    public string? JobId { get; init; }
    public int? Attempt { get; init; }
    public string? Stage { get; init; }
    public double? Progress { get; init; }
    public long DroppedCount { get; init; }
    public string Source { get; init; } = "Worker";
}
