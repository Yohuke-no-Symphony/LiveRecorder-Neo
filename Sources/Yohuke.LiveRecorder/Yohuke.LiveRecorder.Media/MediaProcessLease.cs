namespace Yohuke.LiveRecorder.Media;

internal sealed class MediaProcessLease(Action release) : IDisposable
{
    private Action? release = release;

    public void Dispose()
    {
        Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
