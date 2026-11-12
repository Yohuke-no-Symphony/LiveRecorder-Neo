using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>需要显式隔离 MongoDB 与本机媒体工具的录制回归。</summary>
public sealed class RecordingFactAttribute : FactAttribute
{
    /// <summary>未指定隔离数据库时跳过真实录制回归。</summary>
    public RecordingFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")))
        {
            Skip = "Set LIVE_RECORDER_TEST_MONGO to a disposable MongoDB instance; FFmpeg and ffprobe must be available.";
        }
    }
}
