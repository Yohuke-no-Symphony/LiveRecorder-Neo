using System.Diagnostics;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class TransferThrottle
{
    private readonly Lock sync = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan next;

    internal async Task WaitAsync(int bytes, long bytesPerSecond, CancellationToken cancellationToken)
    {
        if (bytesPerSecond <= 0)
        {
            return;
        }
        TimeSpan wait;
        lock (sync)
        {
            var now = clock.Elapsed;
            next = (next > now ? next : now) + TimeSpan.FromSeconds((double)bytes / bytesPerSecond);
            wait = next - now;
        }
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }
}
