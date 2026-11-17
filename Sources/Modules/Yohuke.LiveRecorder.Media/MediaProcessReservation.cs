namespace Yohuke.LiveRecorder.Media;

internal sealed class MediaProcessReservation(string tool, IDisposable lease) : IDisposable
{
    private IDisposable? lease = lease;
    private int started;

    internal Task<MediaProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task>? input = null)
    {
        ObjectDisposedException.ThrowIf(lease is null, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("A reserved process slot can only start one media process.");
        }
        return MediaProcessRunner.RunProcessAsync(tool, arguments, cancellationToken, input);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref lease, null)?.Dispose();
    }
}
