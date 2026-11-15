namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>带有节点进程顺序和可选任务归属的运行日志。</summary>
public sealed record WorkerLogEntry
{
    public long Sequence { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = "";
    public string? JobId { get; set; }
    public int? Attempt { get; set; }
    public string? Stage { get; set; }
    public double? Progress { get; set; }
}
