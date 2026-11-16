namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class TransferPauseLease(Action release) : IDisposable
{
    private Action? releaseAction = release;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke();
}
