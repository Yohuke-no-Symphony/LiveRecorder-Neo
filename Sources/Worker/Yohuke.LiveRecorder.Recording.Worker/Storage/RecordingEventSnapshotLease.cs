namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed class RecordingEventSnapshotLease(RecordingSpoolSession session, Action release) : IDisposable
{
    private Action? releaseAction = release;
    internal RecordingSpoolSession Session { get; } = session;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke();
}
