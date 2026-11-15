namespace Yohuke.LiveRecorder.Transcoding.Worker.Storage;

internal sealed class WorkerWorkspaceLease(string sourcePath, string outputDirectory, Action release) : IDisposable
{
    private Action? releaseAction = release;
    internal string SourcePath { get; } = sourcePath;
    internal string OutputDirectory { get; } = outputDirectory;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke();
}
