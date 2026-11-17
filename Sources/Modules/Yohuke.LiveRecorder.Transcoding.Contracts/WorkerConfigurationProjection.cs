using System.Security.Cryptography;
using System.Text.Json;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>Bridges user-facing task-specific configuration models with the compatible control protocol model.</summary>
public static class WorkerConfigurationProjection
{
    /// <summary>Creates the transcoding configuration exposed to users.</summary>
    public static TranscodingWorkerConfiguration ToTranscoding(WorkerConfiguration value) => new()
    {
        FfmpegPath = value.FfmpegPath, FfprobePath = value.FfprobePath, TempDirectory = value.TempDirectory,
        MaxConcurrentEncodes = value.MaxConcurrentEncodes, GpuConcurrency = value.GpuConcurrency,
        CpuConcurrency = value.CpuConcurrency, CpuFallbackMode = value.CpuFallbackMode, GpuWaitSeconds = value.GpuWaitSeconds,
        CpuLoadThreshold = value.CpuLoadThreshold, CpuSampleWindowSeconds = value.CpuSampleWindowSeconds,
        CpuThreadFraction = value.CpuThreadFraction, PreferredEncoder = value.PreferredEncoder, DeviceId = value.DeviceId,
        MaxCacheBytes = value.MaxCacheBytes, MinimumFreeBytes = value.MinimumFreeBytes,
        DownloadBytesPerSecond = value.DownloadBytesPerSecond, UploadBytesPerSecond = value.UploadBytesPerSecond
    };

    /// <summary>Creates the clipping configuration exposed to users.</summary>
    public static ClippingWorkerConfiguration ToClipping(WorkerConfiguration value) => new()
    {
        FfmpegPath = value.FfmpegPath, FfprobePath = value.FfprobePath, TempDirectory = value.TempDirectory,
        MaxConcurrentEncodes = value.MaxConcurrentEncodes, ClipEncodingThreads = value.ClipEncodingThreads,
        MaxCacheBytes = value.MaxCacheBytes, MinimumFreeBytes = value.MinimumFreeBytes,
        DownloadBytesPerSecond = value.DownloadBytesPerSecond, UploadBytesPerSecond = value.UploadBytesPerSecond
    };

    /// <summary>Creates the recording configuration exposed to users.</summary>
    public static RecordingWorkerConfiguration ToRecording(WorkerConfiguration value) => new()
    {
        TempDirectory = value.TempDirectory, MaxConcurrentEncodes = value.MaxConcurrentEncodes,
        MaxCacheBytes = value.MaxCacheBytes, MinimumFreeBytes = value.MinimumFreeBytes,
        UploadBytesPerSecond = value.UploadBytesPerSecond
    };

    /// <summary>Adapts transcoding user settings for the compatible control protocol.</summary>
    public static WorkerConfiguration ToLegacy(TranscodingWorkerConfiguration value) => new()
    {
        FfmpegPath = value.FfmpegPath, FfprobePath = value.FfprobePath, TempDirectory = value.TempDirectory,
        MaxConcurrentEncodes = value.MaxConcurrentEncodes, GpuConcurrency = value.GpuConcurrency,
        CpuConcurrency = value.CpuConcurrency, CpuFallbackMode = value.CpuFallbackMode, GpuWaitSeconds = value.GpuWaitSeconds,
        CpuLoadThreshold = value.CpuLoadThreshold, CpuSampleWindowSeconds = value.CpuSampleWindowSeconds,
        CpuThreadFraction = value.CpuThreadFraction, PreferredEncoder = value.PreferredEncoder, DeviceId = value.DeviceId,
        MaxCacheBytes = value.MaxCacheBytes, MinimumFreeBytes = value.MinimumFreeBytes,
        DownloadBytesPerSecond = value.DownloadBytesPerSecond, UploadBytesPerSecond = value.UploadBytesPerSecond
    };

    /// <summary>Adapts clipping user settings for the compatible control protocol.</summary>
    public static WorkerConfiguration ToLegacy(ClippingWorkerConfiguration value) => new()
    {
        FfmpegPath = value.FfmpegPath, FfprobePath = value.FfprobePath, TempDirectory = value.TempDirectory,
        MaxConcurrentEncodes = value.MaxConcurrentEncodes, ClipEncodingThreads = value.ClipEncodingThreads,
        MaxCacheBytes = value.MaxCacheBytes, MinimumFreeBytes = value.MinimumFreeBytes,
        DownloadBytesPerSecond = value.DownloadBytesPerSecond, UploadBytesPerSecond = value.UploadBytesPerSecond
    };

    /// <summary>Adapts recording user settings for the compatible control protocol.</summary>
    public static WorkerConfiguration ToLegacy(RecordingWorkerConfiguration value) => new()
    {
        TempDirectory = value.TempDirectory, MaxConcurrentEncodes = value.MaxConcurrentEncodes,
        MaxCacheBytes = value.MaxCacheBytes, MinimumFreeBytes = value.MinimumFreeBytes,
        UploadBytesPerSecond = value.UploadBytesPerSecond
    };

    /// <summary>Discards fields that have no meaning for the selected task kind.</summary>
    public static WorkerConfiguration Normalize(WorkerConfiguration value, string kind) => kind switch
    {
        "Transcoding" => ToLegacy(ToTranscoding(value)),
        "Clipping" => ToLegacy(ToClipping(value)),
        "Recording" => ToLegacy(ToRecording(value)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Worker kind.")
    };

    /// <summary>Projects configuration to its independent task-specific API model.</summary>
    public static JsonElement Project(WorkerConfiguration value, string kind)
    {
        var model = UserConfiguration(value, kind);
        return JsonSerializer.SerializeToElement(model, model.GetType(), WorkerProtocol.JsonOptions);
    }

    /// <summary>Hashes only the selected user configuration model for stable local and remote synchronization.</summary>
    public static string ComputeHash(WorkerConfiguration value, string kind)
    {
        var model = UserConfiguration(value, kind);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(model, model.GetType())));
    }

    /// <summary>Creates settings serialization options that write task-specific configuration and read legacy files.</summary>
    public static JsonSerializerOptions CreateJsonOptions(string kind, JsonSerializerOptions? template = null)
    {
        _ = UserConfiguration(new(), kind);
        var options = template is null ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions(template);
        for (var index = options.Converters.Count - 1; index >= 0; index--)
        {
            if (options.Converters[index] is WorkerConfigurationJsonConverter)
            {
                options.Converters.RemoveAt(index);
            }
        }
        options.Converters.Insert(0, new WorkerConfigurationJsonConverter(kind));
        return options;
    }

    internal static object UserConfiguration(WorkerConfiguration value, string kind) => kind switch
    {
        "Transcoding" => ToTranscoding(value),
        "Clipping" => ToClipping(value),
        "Recording" => ToRecording(value),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Worker kind.")
    };
}
