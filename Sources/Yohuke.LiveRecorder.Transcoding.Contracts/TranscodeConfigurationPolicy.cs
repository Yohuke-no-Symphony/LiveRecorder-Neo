namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>主站和 Worker 共用的资源与编码配置约束。</summary>
public static class TranscodeConfigurationPolicy
{
    /// <summary>校验主站下发的资源配置，拒绝无界或不可解释的运行参数。</summary>
    public static void Validate(WorkerConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.FfmpegPath) || string.IsNullOrWhiteSpace(value.FfprobePath)
            || value.FfmpegPath.Length > 4096 || value.FfprobePath.Length > 4096 || value.TempDirectory is null or { Length: > 4096 }
            || value.MaxConcurrentEncodes is < 1 or > 64 || value.GpuConcurrency is < 0 or > 64
            || value.CpuConcurrency is < 0 or > 64 || value.GpuConcurrency + value.CpuConcurrency == 0
            || value.CpuFallbackMode is not ("Balanced" or "Disabled")
            || value.GpuWaitSeconds is < 0 or > 86400 || value.CpuSampleWindowSeconds is < 5 or > 300
            || !double.IsFinite(value.CpuLoadThreshold) || value.CpuLoadThreshold is <= 0 or > 100
            || !double.IsFinite(value.CpuThreadFraction) || value.CpuThreadFraction is <= 0 or > 1
            || value.MaxCacheBytes < 1024 * 1024 || value.MinimumFreeBytes < 0
            || value.DownloadBytesPerSecond < 0 || value.UploadBytesPerSecond < 0
            || value.PreferredEncoder is not ("auto" or "h264_qsv" or "h264_nvenc" or "h264_amf" or "h264_vaapi" or "h264_videotoolbox")
            || value.DeviceId?.Length > 1024)
        {
            throw new ArgumentException("Invalid worker resource configuration.", nameof(value));
        }
    }

    /// <summary>验证独立清晰度的稳定标识及平台无关编码参数。</summary>
    public static void ValidateProfile(TranscodeProfile value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 64 || value.Id == "original"
            || value.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 80
            || value.Height is < 64 or > 8640 || value.Height % 2 != 0
            || value.VideoBitrateKbps is < 32 or > 1000000 || value.AudioBitrateKbps is < 16 or > 512
            || (value.MaxFrameRate is { } frameRate && (!double.IsFinite(frameRate) || frameRate is <= 0 or > 240)))
        {
            throw new ArgumentException("Invalid transcoding profile.", nameof(value));
        }
    }
}
