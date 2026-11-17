namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class RecordingOffsetTimeProvider(TimeProvider inner) : TimeProvider
{
    internal TimeSpan UtcOffset { get; set; }

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + UtcOffset;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}
