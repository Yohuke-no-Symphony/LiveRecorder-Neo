namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>Contains the user-facing settings consumed by a clipping Worker.</summary>
public sealed record ClippingWorkerConfiguration
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string TempDirectory { get; set; } = "";
    public int MaxConcurrentEncodes { get; set; } = 2;
    public int ClipEncodingThreads { get; set; } = 2;
    public long MaxCacheBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long DownloadBytesPerSecond { get; set; }
    public long UploadBytesPerSecond { get; set; }
}
