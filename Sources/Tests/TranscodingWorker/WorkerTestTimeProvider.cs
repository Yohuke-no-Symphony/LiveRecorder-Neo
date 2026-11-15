namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

internal sealed class WorkerTestTimeProvider : TimeProvider
{
    internal TimeSpan Elapsed { get; set; }

    /// <summary>提供测试使用的单调时钟频率。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>返回测试推进后的单调时间戳。</summary>
    public override long GetTimestamp() => Elapsed.Ticks;
}
