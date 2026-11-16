namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>Contains the user-facing settings consumed by a recording Worker.</summary>
public sealed record RecordingWorkerConfiguration
{
    public string TempDirectory { get; set; } = "";
    public int MaxConcurrentEncodes { get; set; } = 2;
    public long MaxCacheBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long UploadBytesPerSecond { get; set; }
}
