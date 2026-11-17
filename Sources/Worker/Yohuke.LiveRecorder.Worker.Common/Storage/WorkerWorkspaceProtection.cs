namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal sealed class WorkerWorkspaceProtection(Action release) : IDisposable
{
    private Action? releaseAction = release;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke();
}
