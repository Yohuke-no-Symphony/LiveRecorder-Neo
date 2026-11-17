namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class RecordingLifecycleTimeProvider(DateTimeOffset startedAtUtc) : TimeProvider
{
    private DateTimeOffset utcNow = startedAtUtc;
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override long GetTimestamp() => timestamp;

    internal void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        utcNow = utcNow.Add(duration);
        timestamp = checked(timestamp + duration.Ticks);
    }

    internal void ShiftUtc(TimeSpan duration)
    {
        utcNow = utcNow.Add(duration);
    }
}
