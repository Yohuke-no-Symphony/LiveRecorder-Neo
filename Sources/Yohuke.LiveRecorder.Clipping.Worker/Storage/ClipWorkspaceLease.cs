namespace Yohuke.LiveRecorder.Clipping.Worker.Storage;

internal sealed class ClipWorkspaceLease(string directory, Action release) : IDisposable
{
    private Action? releaseAction = release;
    internal string DirectoryPath { get; } = directory;
    internal string OutputPath => Path.Combine(DirectoryPath, "result.mp4");

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke();
}
