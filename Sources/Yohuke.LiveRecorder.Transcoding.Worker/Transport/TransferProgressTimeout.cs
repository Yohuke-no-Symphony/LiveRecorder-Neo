using System.Diagnostics;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class TransferProgressTimeout : IDisposable
{
    private readonly Lock sync = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource cancellation;
    private readonly Timer timer;
    private readonly TimeSpan timeout;
    private TimeSpan lastProgress;
    private int paused;
    private bool disposed;

    internal CancellationToken Token => cancellation.Token;

    internal TransferProgressTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        this.timeout = timeout;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 4, 10, 1000));
        timer = new(Check, null, interval, interval);
    }

    internal void Progress()
    {
        lock (sync)
        {
            lastProgress = clock.Elapsed;
        }
    }

    internal IDisposable Pause()
    {
        lock (sync)
        {
            paused++;
        }
        return new TransferPauseLease(() =>
        {
            lock (sync)
            {
                paused--;
                lastProgress = clock.Elapsed;
            }
        });
    }

    private void Check(object? state)
    {
        lock (sync)
        {
            if (disposed || paused > 0 || clock.Elapsed - lastProgress < timeout)
            {
                return;
            }
            cancellation.Cancel();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            timer.Dispose();
            cancellation.Dispose();
        }
    }
}
