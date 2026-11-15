using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal static class WorkerServiceProcess
{
    internal static async Task<WorkerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, int timeoutSeconds = 30, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await new WorkerProcessRunner().RunAsync(executable, arguments, timeout.Token, environment: environment);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkerServiceException("The service manager command timed out.", exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            var failure = new WorkerServiceException("Unable to execute the system service manager.", exception);
            failure.Data["NativeMessage"] = exception.Message;
            failure.Data["NativeErrorCode"] = exception.NativeErrorCode;
            throw failure;
        }
    }

    internal static void RequireSuccess(WorkerProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }
        var failure = new WorkerServiceException($"The service manager could not {operation}. Exit code {result.ExitCode}.");
        failure.Data["NativeExitCode"] = result.ExitCode;
        failure.Data["StandardOutput"] = result.Output;
        failure.Data["StandardError"] = result.Diagnostic;
        throw failure;
    }

    internal static async Task ExecuteAsync(string executable, IReadOnlyList<string> arguments, string operation,
        CancellationToken cancellationToken, int timeoutSeconds = 30)
    {
        RequireSuccess(await RunAsync(executable, arguments, cancellationToken, timeoutSeconds), operation);
    }
}
