namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>Validates shared Worker resources while respecting the registered task kind.</summary>
public static class WorkerConfigurationPolicy
{
    /// <summary>Validates only settings consumed by the registered Worker kind.</summary>
    public static void Validate(WorkerConfiguration value, string kind)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (kind == "Recording")
        {
            if (value.TempDirectory is null or { Length: > 4096 } || value.MaxConcurrentEncodes is < 1 or > 64
                || value.MaxCacheBytes < 1024 * 1024 || value.MinimumFreeBytes < 0 || value.UploadBytesPerSecond < 0)
            {
                throw new ArgumentException("Invalid recording Worker resource configuration.", nameof(value));
            }
            return;
        }
        if (kind == "Transcoding")
        {
            TranscodeConfigurationPolicy.Validate(value);
            return;
        }
        if (kind != "Clipping" || string.IsNullOrWhiteSpace(value.FfmpegPath) || string.IsNullOrWhiteSpace(value.FfprobePath)
            || value.FfmpegPath.Length > 4096 || value.FfprobePath.Length > 4096
            || value.TempDirectory is null or { Length: > 4096 } || value.MaxConcurrentEncodes is < 1 or > 64
            || value.MaxCacheBytes < 1024 * 1024 || value.MinimumFreeBytes < 0
            || value.ClipEncodingThreads is < 1 or > 128
            || value.DownloadBytesPerSecond < 0 || value.UploadBytesPerSecond < 0)
        {
            throw new ArgumentException("Invalid worker resource configuration.", nameof(value));
        }
    }
}
