namespace Yohuke.LiveRecorder.Tests;

internal sealed class WorkerStatusTimeProvider : TimeProvider
{
    private long timestamp;
    private DateTimeOffset utc = DateTimeOffset.UtcNow;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public override DateTimeOffset GetUtcNow() => utc;
    internal void Advance(TimeSpan value) => timestamp += value.Ticks;
    internal void ChangeWallClock(TimeSpan value) => utc += value;
}
