namespace Yohuke.LiveRecorder.AI.Worker.Tests;

internal sealed class AiTestTimeProvider : TimeProvider
{
    private long timestamp;
    internal DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override long GetTimestamp() => timestamp;

    internal void Advance(TimeSpan duration)
    {
        timestamp += duration.Ticks;
        UtcNow += duration;
    }
}
