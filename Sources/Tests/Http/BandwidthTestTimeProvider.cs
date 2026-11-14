namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class BandwidthTestTimeProvider : TimeProvider
{
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(timestamp);

    internal void Advance(TimeSpan interval) => timestamp += interval.Ticks;
}
