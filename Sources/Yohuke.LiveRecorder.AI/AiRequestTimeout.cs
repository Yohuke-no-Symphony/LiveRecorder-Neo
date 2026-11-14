namespace Yohuke.LiveRecorder.AI;

internal sealed class AiRequestTimeout : IDisposable
{
    private static readonly TimeSpan MAX_TIMER_DELAY = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource cancellation;
    private readonly TimeSpan idleTimeout;
    private readonly ITimer? timer;
    private long lastActivity;
    private bool disposed;

    internal AiRequestTimeout(TimeProvider clock, int seconds, CancellationToken callerToken)
    {
        this.clock = clock;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        idleTimeout = TimeSpan.FromSeconds(seconds);
        if (seconds > 0)
        {
            timer = clock.CreateTimer(CheckTimeout, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            RecordActivity();
        }
    }

    internal CancellationToken Token => cancellation.Token;

    internal void RecordActivity()
    {
        lock (gate)
        {
            if (disposed || cancellation.IsCancellationRequested)
            {
                return;
            }
            lastActivity = clock.GetTimestamp();
            timer?.Change(NextDelay(idleTimeout), Timeout.InfiniteTimeSpan);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            cancellation.Dispose();
        }
    }

    private void CheckTimeout(object? state)
    {
        lock (gate)
        {
            if (disposed || cancellation.IsCancellationRequested)
            {
                return;
            }
            var remaining = idleTimeout - clock.GetElapsedTime(lastActivity);
            if (remaining > TimeSpan.Zero)
            {
                timer!.Change(NextDelay(remaining), Timeout.InfiniteTimeSpan);
                return;
            }
        }
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static TimeSpan NextDelay(TimeSpan remaining) => remaining < MAX_TIMER_DELAY ? remaining : MAX_TIMER_DELAY;
}
