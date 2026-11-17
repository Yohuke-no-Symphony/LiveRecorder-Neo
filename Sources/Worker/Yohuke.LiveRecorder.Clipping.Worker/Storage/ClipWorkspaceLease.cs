namespace Yohuke.LiveRecorder.Clipping.Worker.Storage;

internal sealed class ClipWorkspaceLease(string directory, Action<bool> release) : IDisposable
{
    private Action<bool>? releaseAction = release;
    private bool completed;
    internal string DirectoryPath { get; } = directory;
    internal string OutputPath { get; private set; } = Path.Combine(directory, "result.mp4");

    internal void SelectOutput(string path) => OutputPath = path;

    internal void Complete() => completed = true;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke(completed);
}
