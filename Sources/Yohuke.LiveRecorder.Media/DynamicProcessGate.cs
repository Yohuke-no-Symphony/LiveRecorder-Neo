namespace Yohuke.LiveRecorder.Media;

internal sealed class DynamicProcessGate : IDisposable
{
    private readonly object sync = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private bool disposed;

    internal async Task<IDisposable> AcquireAsync(int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task wait;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (active < Math.Max(1, limit))
                {
                    active++;
                    return new MediaProcessLease(Release);
                }
                wait = changed.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    private void Release()
    {
        TaskCompletionSource completed;
        lock (sync)
        {
            active--;
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
