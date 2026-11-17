namespace Yohuke.LiveRecorder.AI.Contracts;

/// <summary>AI 执行节点使用的本地缓存、任务并发和传输设置。</summary>
public sealed record AiWorkerConfiguration
{
    public string TempDirectory { get; set; } = "";
    public int MaxConcurrentJobs { get; set; } = 1;
    public long MaxCacheBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long DownloadBytesPerSecond { get; set; }
    public long UploadBytesPerSecond { get; set; }
}
