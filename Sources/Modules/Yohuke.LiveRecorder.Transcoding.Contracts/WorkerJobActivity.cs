namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>当前任务阶段中可用的传输或编码指标。</summary>
public sealed record WorkerJobActivity
{
    public long? BytesTransferred { get; set; }
    public long? TotalBytes { get; set; }
    public double? BytesPerSecond { get; set; }
    public double? ProcessedDurationMs { get; set; }
    public double? TotalDurationMs { get; set; }
    public double? FramesPerSecond { get; set; }
    public double? Speed { get; set; }
    public double? EstimatedSecondsRemaining { get; set; }
    public string? CurrentFile { get; set; }
    public int? FilesCompleted { get; set; }
    public int? FilesTotal { get; set; }
}
