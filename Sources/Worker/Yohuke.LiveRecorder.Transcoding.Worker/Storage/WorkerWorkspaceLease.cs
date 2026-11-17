namespace Yohuke.LiveRecorder.Transcoding.Worker.Storage;

internal sealed class WorkerWorkspaceLease(string sourcePath, string jobDirectory, Action<bool> release) : IDisposable
{
    private Action<bool>? releaseAction = release;
    private bool completed;
    internal string SourcePath { get; } = sourcePath;
    internal string JobDirectory { get; } = jobDirectory;
    internal string OutputDirectory { get; private set; } = jobDirectory;

    internal void SelectOutput(string directory) => OutputDirectory = directory;

    internal void Complete() => completed = true;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke(completed);
}
