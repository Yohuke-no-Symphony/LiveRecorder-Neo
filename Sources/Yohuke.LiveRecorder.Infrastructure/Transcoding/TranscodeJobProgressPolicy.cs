using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>按任务阶段规范化节点上报的诊断进度。</summary>
public static class TranscodeJobProgressPolicy
{
    private const int MAX_ARTIFACT_COUNT = 100000;
    private const int MAX_FILE_NAME_LENGTH = 256;

    /// <summary>保留支持的执行阶段，并为未知阶段提供运行状态。</summary>
    public static string NormalizeStage(string stage) => stage is "Downloading" or "Inspecting" or "WaitingForGpu"
        or "Encoding" or "Uploading" or "Validating" or "Finalizing" ? stage : "Running";

    /// <summary>仅保留当前阶段适用且有效的数值，诊断信息无效时允许任务继续续租。</summary>
    public static WorkerJobActivity? NormalizeActivity(string stage, WorkerJobActivity? activity)
    {
        if (activity is null)
        {
            return null;
        }
        if (stage == "Encoding")
        {
            if (!IsNonnegative(activity.ProcessedDurationMs) || !IsNonnegative(activity.TotalDurationMs)
                || !IsNonnegative(activity.FramesPerSecond) || !IsNonnegative(activity.Speed)
                || !IsNonnegative(activity.EstimatedSecondsRemaining))
            {
                return null;
            }
            var processed = activity.ProcessedDurationMs;
            if (processed is { } position && activity.TotalDurationMs is { } duration)
            {
                processed = Math.Min(position, duration);
            }
            return new()
            {
                ProcessedDurationMs = processed,
                TotalDurationMs = activity.TotalDurationMs,
                FramesPerSecond = activity.FramesPerSecond,
                Speed = activity.Speed,
                EstimatedSecondsRemaining = activity.EstimatedSecondsRemaining
            };
        }
        if (stage is not ("Downloading" or "Uploading"))
        {
            return null;
        }
        if (activity.BytesTransferred is < 0 || activity.TotalBytes is < 0
            || activity.BytesTransferred is { } completedBytes && activity.TotalBytes is { } totalBytes && completedBytes > totalBytes
            || !IsNonnegative(activity.BytesPerSecond) || !IsNonnegative(activity.EstimatedSecondsRemaining)
            || activity.FilesCompleted is < 0 or > MAX_ARTIFACT_COUNT || activity.FilesTotal is < 0 or > MAX_ARTIFACT_COUNT
            || activity.FilesCompleted is { } completedFiles && activity.FilesTotal is { } totalFiles && completedFiles > totalFiles
            || activity.CurrentFile is { } fileName && (fileName.Length > MAX_FILE_NAME_LENGTH || fileName.Any(char.IsControl)))
        {
            return null;
        }
        return new()
        {
            BytesTransferred = activity.BytesTransferred,
            TotalBytes = activity.TotalBytes,
            BytesPerSecond = activity.BytesPerSecond,
            EstimatedSecondsRemaining = activity.EstimatedSecondsRemaining,
            CurrentFile = string.IsNullOrWhiteSpace(activity.CurrentFile) ? null : activity.CurrentFile,
            FilesCompleted = activity.FilesCompleted,
            FilesTotal = activity.FilesTotal
        };
    }

    private static bool IsNonnegative(double? value) => value is not { } number || double.IsFinite(number) && number >= 0;
}
