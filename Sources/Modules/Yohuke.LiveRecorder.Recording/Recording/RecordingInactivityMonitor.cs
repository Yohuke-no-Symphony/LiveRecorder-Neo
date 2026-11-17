namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>以单调时间限制跨连接重试的连续无媒体时长。</summary>
public sealed class RecordingInactivityMonitor : IDisposable
{
    private readonly Lock gate = new();
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource cancellation;
    private readonly ITimer timer;
    private TimeSpan timeout;
    private long lastActivity;
    private bool isExpired;
    private bool disposed;
    private Task cancellationCallbacks = Task.CompletedTask;

    /// <summary>从当前时刻开始计时，并关联录制目标的取消令牌。</summary>
    public RecordingInactivityMonitor(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        this.timeout = timeout;
        this.timeProvider = timeProvider;
        lastActivity = timeProvider.GetTimestamp();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Token = cancellation.Token;
        timer = timeProvider.CreateTimer(_ => CheckDeadline(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(timeout, Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token { get; }
    public bool IsExpired
    {
        get
        {
            lock (gate)
            {
                return isExpired;
            }
        }
    }

    /// <summary>有效媒体到达时重新计时，已到期的场次不会被重新激活。</summary>
    public void ReportActivity()
    {
        lock (gate)
        {
            if (disposed || isExpired || Token.IsCancellationRequested)
            {
                return;
            }
            if (timeProvider.GetElapsedTime(lastActivity) >= timeout)
            {
                Expire();
                return;
            }
            lastActivity = timeProvider.GetTimestamp();
            timer.Change(timeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>更新等待上限，保留最近有效媒体的接收时刻。</summary>
    public void Configure(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
        lock (gate)
        {
            if (disposed || isExpired)
            {
                return;
            }
            timeout = value;
            var remaining = timeout - timeProvider.GetElapsedTime(lastActivity);
            if (remaining <= TimeSpan.Zero)
            {
                Expire();
                return;
            }
            timer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckDeadline()
    {
        lock (gate)
        {
            if (disposed || isExpired)
            {
                return;
            }
            var remaining = timeout - timeProvider.GetElapsedTime(lastActivity);
            if (remaining > TimeSpan.Zero)
            {
                timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            Expire();
        }
    }

    private void Expire()
    {
        isExpired = true;
        cancellationCallbacks = cancellation.CancelAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task callbacks;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            timer.Dispose();
            callbacks = cancellationCallbacks;
        }
        try
        {
            callbacks.GetAwaiter().GetResult();
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
