namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiManualTimeProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<AiManualTimer> timers = [];
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return timestamp;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new AiManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        var target = checked(GetTimestamp() + duration.Ticks);
        while (true)
        {
            Action? fire;
            lock (gate)
            {
                var next = timers.Where(timer => timer.DueAtTicks <= target).MinBy(timer => timer.DueAtTicks);
                if (next is null)
                {
                    timestamp = target;
                    return;
                }
                timestamp = Math.Max(timestamp, next.DueAtTicks);
                fire = next.TakeCallback(timestamp);
            }
            fire?.Invoke();
        }
    }

    internal bool Change(AiManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            if (timer.IsDisposed)
            {
                return false;
            }
            timer.DueAtTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(timestamp + dueTime.Ticks);
            timer.PeriodTicks = period <= TimeSpan.Zero ? 0 : period.Ticks;
            if (!timers.Contains(timer))
            {
                timers.Add(timer);
            }
            return true;
        }
    }

    internal void Dispose(AiManualTimer timer)
    {
        lock (gate)
        {
            timer.IsDisposed = true;
            timers.Remove(timer);
        }
    }
}
