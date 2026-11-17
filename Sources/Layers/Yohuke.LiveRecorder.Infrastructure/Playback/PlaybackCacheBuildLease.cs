namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal sealed class PlaybackCacheBuildLease(Action release) : IDisposable
{
    private Action? release = release;

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
