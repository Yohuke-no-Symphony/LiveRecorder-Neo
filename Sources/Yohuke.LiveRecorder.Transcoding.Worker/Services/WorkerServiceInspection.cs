using System.Globalization;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal static class WorkerServiceInspection
{
    internal const int RECENT_LOG_COUNT = 100;

    internal static async Task<int> ExecuteAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == WorkerCommand.Status)
            {
                var status = await GetStatusAsync(cancellationToken);
                Console.WriteLine("Service: " + WorkerServices.SERVICE_NAME);
                Console.WriteLine("Installed: " + (status.Installed ? "Yes" : "No"));
                Console.WriteLine("State: " + status.State);
                Console.WriteLine("Startup: " + status.Startup);
                Console.WriteLine("Process ID: " + (status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-"));
                Console.WriteLine("Logs: " + status.LogSource);
                return status.IsRunning ? 0 : 1;
            }
            if (command != WorkerCommand.Log)
            {
                throw new ArgumentOutOfRangeException(nameof(command));
            }
            if (OperatingSystem.IsWindows())
            {
                await WindowsWorkerInspection.FollowLogAsync(cancellationToken);
            }
            else if (OperatingSystem.IsMacOS())
            {
                await MacWorkerInspection.FollowLogAsync(cancellationToken);
            }
            else if (OperatingSystem.IsLinux())
            {
                await LinuxWorkerInspection.FollowLogAsync(cancellationToken);
            }
            else
            {
                throw new WorkerServiceException("Service inspection is supported on macOS, Linux with systemd, and Windows.");
            }
            return 0;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new WorkerServiceException("Access to the worker service information or logs was denied. Use an administrator terminal.", exception);
        }
        catch (Exception exception) when (exception is not WorkerServiceException and not OperationCanceledException)
        {
            var error = new WorkerServiceException("Unable to read the worker service information or logs. Check access permissions and the system service manager.", exception);
            error.Data["NativeMessage"] = exception.Message;
            throw error;
        }
    }

    private static Task<WorkerServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsWorkerInspection.GetStatusAsync(cancellationToken);
        }
        if (OperatingSystem.IsMacOS())
        {
            return MacWorkerInspection.GetStatusAsync(cancellationToken);
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxWorkerInspection.GetStatusAsync(cancellationToken);
        }
        throw new WorkerServiceException("Service inspection is supported on macOS, Linux with systemd, and Windows.");
    }
}
