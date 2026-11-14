namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiManualTimer(AiManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
{
    internal long DueAtTicks { get; set; } = long.MaxValue;
    internal long PeriodTicks { get; set; }
    internal bool IsDisposed { get; set; }

    public bool Change(TimeSpan dueTime, TimeSpan period) => clock.Change(this, dueTime, period);

    public void Dispose() => clock.Dispose(this);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal Action? TakeCallback(long timestamp)
    {
        if (IsDisposed || DueAtTicks > timestamp)
        {
            return null;
        }
        DueAtTicks = PeriodTicks == 0 ? long.MaxValue : checked(timestamp + PeriodTicks);
        return () => callback(state);
    }
}
