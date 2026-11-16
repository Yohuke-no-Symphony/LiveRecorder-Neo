using System.Runtime.InteropServices;
using System.Security.Principal;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal static class WorkerServices
{
    [DllImport("libc")]
    private static extern uint geteuid();

    internal static Task ExecuteAsync(WorkerCommand command, string configurationPath, CancellationToken cancellationToken) =>
        ExecuteAsync(command, configurationPath, WorkerServiceIdentity.Transcoding, cancellationToken);

    internal static async Task ExecuteAsync(WorkerCommand command, string configurationPath, WorkerServiceIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            {
                throw new WorkerServiceException("System service management is supported on macOS, Linux with systemd, and Windows.");
            }
            RequireAdministrator();
            var directory = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Yohuke.LiveRecorder", identity.DirectoryName)
                : OperatingSystem.IsMacOS() ? identity.MacDefinitionDirectory : "/var/lib/" + identity.ServiceName;
            using var operation = new WorkerServiceFiles(identity).AcquireOperation(directory);
            if (OperatingSystem.IsWindows())
            {
                await new WindowsWorkerService(identity).ExecuteAsync(command, configurationPath, cancellationToken);
            }
            else if (OperatingSystem.IsMacOS())
            {
                await new MacWorkerService(identity).ExecuteAsync(command, configurationPath, cancellationToken);
            }
            else if (OperatingSystem.IsLinux())
            {
                await new LinuxWorkerService(identity).ExecuteAsync(command, configurationPath, cancellationToken);
            }
            else
            {
                throw new WorkerServiceException("System service management is supported on macOS, Linux with systemd, and Windows.");
            }
            Console.WriteLine(command switch
            {
                WorkerCommand.Install => "Worker service is installed. Use 'start' to run it and 'enable' for automatic startup.",
                WorkerCommand.Uninstall => "Worker service is uninstalled.",
                WorkerCommand.Start => "Worker service is running.",
                WorkerCommand.Stop => "Worker service is stopped.",
                WorkerCommand.Enable => "Worker service will start automatically at boot.",
                WorkerCommand.Disable => "Worker service startup is manual.",
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            });
        }
        catch (Exception exception) when (exception is not WorkerServiceException and not OperationCanceledException)
        {
            var failure = new WorkerServiceException("Unable to complete the service operation. Check administrator privileges and access to the service files.", exception);
            failure.Data["NativeMessage"] = exception.Message;
            throw failure;
        }
    }

    internal static void RequireAdministrator()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new WorkerServiceException("Run service management commands from an administrator terminal.");
            }
            return;
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            if (geteuid() != 0)
            {
                throw new WorkerServiceException("Run system service management commands as root, using sudo.");
            }
        }
    }
}
