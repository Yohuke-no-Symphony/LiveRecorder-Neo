namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>主站管理的 Worker 运行配置。</summary>
public sealed record WorkerConfiguration
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string TempDirectory { get; set; } = "";
    public int MaxConcurrentEncodes { get; set; } = 2;
    public int GpuConcurrency { get; set; } = 1;
    public int CpuConcurrency { get; set; } = 1;
    public string CpuFallbackMode { get; set; } = "Balanced";
    public int GpuWaitSeconds { get; set; } = 60;
    public double CpuLoadThreshold { get; set; } = 60;
    public int CpuSampleWindowSeconds { get; set; } = 30;
    public double CpuThreadFraction { get; set; } = 0.5;
    public string PreferredEncoder { get; set; } = "auto";
    public string? DeviceId { get; set; }
    public long MaxCacheBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long DownloadBytesPerSecond { get; set; }
    public long UploadBytesPerSecond { get; set; }
}
