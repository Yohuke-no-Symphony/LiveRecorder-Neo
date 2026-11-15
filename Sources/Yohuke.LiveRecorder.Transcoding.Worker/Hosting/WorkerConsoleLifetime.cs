using System.Runtime.InteropServices;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

internal sealed class WorkerConsoleLifetime : IDisposable
{
    private readonly Lock sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly PosixSignalRegistration? termination;
    private bool disposed;

    internal WorkerConsoleLifetime()
    {
        if (!OperatingSystem.IsWindows())
        {
            termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTermination);
        }
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    internal CancellationToken Token => cancellation.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        Cancel();
    }

    private void OnTermination(PosixSignalContext context)
    {
        context.Cancel = true;
        Cancel();
    }

    private void Cancel()
    {
        lock (sync)
        {
            if (!disposed)
            {
                cancellation.Cancel();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        termination?.Dispose();
        lock (sync)
        {
            disposed = true;
            cancellation.Dispose();
        }
    }
}
