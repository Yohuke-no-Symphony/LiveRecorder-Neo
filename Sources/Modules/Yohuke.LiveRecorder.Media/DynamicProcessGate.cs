namespace Yohuke.LiveRecorder.Media;

internal sealed class DynamicProcessGate : IDisposable
{
    private readonly object sync = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private int waitingForeground;
    private bool prefetchActive;
    private bool disposed;

    internal async Task<IDisposable> AcquireAsync(int limit, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            waitingForeground++;
        }
        try
        {
            while (true)
            {
                Task wait;
                lock (sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (active < Math.Max(1, limit))
                    {
                        active++;
                        return new MediaProcessLease(() => Release(false));
                    }
                    wait = changed.Task;
                }
                await wait.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            lock (sync)
            {
                waitingForeground--;
            }
        }
    }

    internal IDisposable? TryAcquirePrefetch(int limit, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (limit <= 1 || active >= limit - 1 || prefetchActive || waitingForeground > 0)
            {
                return null;
            }
            active++;
            prefetchActive = true;
            return new MediaProcessLease(() => Release(true));
        }
    }

    private void Release(bool prefetch)
    {
        TaskCompletionSource completed;
        lock (sync)
        {
            active--;
            if (prefetch)
            {
                prefetchActive = false;
            }
            completed = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        completed.TrySetResult();
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            changed.TrySetResult();
        }
    }
}
