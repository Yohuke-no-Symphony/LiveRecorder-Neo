namespace Yohuke.LiveRecorder.Clipping.Worker.Execution;

internal sealed class ClipExportProgress(Action<double>? callback) : IProgress<double>
{
    /// <inheritdoc />
    public void Report(double value) => callback?.Invoke(value);
}
