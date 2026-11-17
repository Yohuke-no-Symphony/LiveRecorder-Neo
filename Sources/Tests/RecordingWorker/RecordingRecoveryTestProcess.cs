using System.Diagnostics;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingRecoveryTestProcess : IAsyncDisposable
{
    internal Process Process { get; }
    private readonly Task<string> output;
    private readonly Task<string> error;

    internal RecordingRecoveryTestProcess(params string[] args)
    {
        Process = System.Diagnostics.Process.Start(RecordingRecoveryTestHost.StartInfo(args))!;
        output = Process.StandardOutput.ReadToEndAsync();
        error = Process.StandardError.ReadToEndAsync();
    }

    internal async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (Process.HasExited)
            {
                throw new InvalidOperationException("Recovery process exited unexpectedly: " + await output + await error);
            }
            await Task.Delay(20, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
        }
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
        Process.Dispose();
    }
}
