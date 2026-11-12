using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>显式开启才运行至少一分钟的真实多路压力。</summary>
public sealed class RecordingStressFactAttribute : FactAttribute
{
    /// <summary>避免日常单测隐式运行长时压力。</summary>
    public RecordingStressFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LIVE_RECORDER_STRESS") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")))
        {
            Skip = "Set LIVE_RECORDER_STRESS=1 and LIVE_RECORDER_TEST_MONGO for the 32-stream, 60-second stress run.";
        }
    }
}
