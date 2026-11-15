using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal static class LinuxWorkerService
{
    internal const string UNIT_PATH = "/etc/systemd/system/" + WorkerServices.SERVICE_NAME + ".service";
    internal const string BUNDLE_DIRECTORY = "/var/cache/" + WorkerServices.SERVICE_NAME + "/bundles";
    internal const string SERVICE_UNIT = WorkerServices.SERVICE_NAME + ".service";

    internal static async Task ExecuteAsync(WorkerCommand command, string configurationPath, CancellationToken cancellationToken)
    {
        var systemctl = GetSystemctlPath();
        if (command == WorkerCommand.Install)
        {
            await InstallAsync(systemctl, WorkerServiceInvocation.Current(configurationPath), cancellationToken);
            return;
        }
        if (command == WorkerCommand.Uninstall)
        {
            await UninstallAsync(systemctl, cancellationToken);
            return;
        }
        await WorkerServiceFiles.ReadManagedAsync(UNIT_PATH, cancellationToken);
        switch (command)
        {
            case WorkerCommand.Start:
                await WorkerServiceProcess.ExecuteAsync(systemctl, ["start", SERVICE_UNIT], "start the worker service", cancellationToken, 85);
                await WorkerServiceProcess.ExecuteAsync(systemctl, ["is-active", "--quiet", SERVICE_UNIT],
                    "confirm that the worker service is running", cancellationToken);
                break;
            case WorkerCommand.Stop:
                await WorkerServiceProcess.ExecuteAsync(systemctl, ["stop", SERVICE_UNIT], "stop the worker service", cancellationToken, 85);
                break;
            case WorkerCommand.Enable:
                await WorkerServiceProcess.ExecuteAsync(systemctl, ["enable", SERVICE_UNIT], "enable automatic startup", cancellationToken);
                break;
            case WorkerCommand.Disable:
                await WorkerServiceProcess.ExecuteAsync(systemctl, ["disable", SERVICE_UNIT], "disable automatic startup", cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    internal static string GetSystemctlPath()
    {
        var systemctl = File.Exists("/usr/bin/systemctl") ? "/usr/bin/systemctl" : "/bin/systemctl";
        if (!File.Exists(systemctl) || !Directory.Exists("/run/systemd/system"))
        {
            throw new WorkerServiceException("Linux system service management requires a running systemd instance.");
        }
        return systemctl;
    }

    internal static string CreateUnit(WorkerServiceInvocation invocation)
    {
        var executable = Quote(invocation.ExecutablePath);
        var arguments = string.Join(' ', invocation.Arguments.Select(Quote));
        var workingDirectory = invocation.WorkingDirectory.Replace("%", "%%", StringComparison.Ordinal) + "/";
        return $"""
            # {WorkerServiceFiles.OWNER_MARKER}
            [Unit]
            Description=Yohuke Live Recorder Transcoding Worker
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=simple
            User=root
            WorkingDirectory={workingDirectory}
            ExecStart=:{executable} {arguments}
            Environment="PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
            Environment="DOTNET_BUNDLE_EXTRACT_BASE_DIR={BUNDLE_DIRECTORY}"
            CacheDirectory={WorkerServices.SERVICE_NAME}
            CacheDirectoryMode=0700
            Restart=on-failure
            RestartSec=5
            TimeoutStopSec=75
            KillMode=mixed

            [Install]
            WantedBy=multi-user.target

            """;
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    private static async Task InstallAsync(string systemctl, WorkerServiceInvocation invocation, CancellationToken cancellationToken)
    {
        var content = CreateUnit(invocation);
        if (File.Exists(UNIT_PATH))
        {
            if (await WorkerServiceFiles.ReadManagedAsync(UNIT_PATH, cancellationToken) != content)
            {
                throw new WorkerServiceException("The worker service is already installed with a different executable or configuration. Run 'uninstall' before installing it again.");
            }
            await ReloadAsync(systemctl, cancellationToken);
            return;
        }
        if (await LoadStateAsync(systemctl, cancellationToken) != "not-found")
        {
            throw new WorkerServiceException("A systemd service with this name is already registered outside the managed unit path.");
        }
        WorkerServiceFiles.CreatePrivateDirectory(BUNDLE_DIRECTORY);
        await WorkerServiceFiles.WriteNewAsync(UNIT_PATH, content, cancellationToken);
        try
        {
            await ReloadAsync(systemctl, cancellationToken);
            await WorkerServiceProcess.ExecuteAsync(systemctl, ["disable", SERVICE_UNIT],
                "set manual startup for the worker service", cancellationToken);
        }
        catch (Exception exception)
        {
            try
            {
                await WorkerServiceFiles.DeleteMatchingAsync(UNIT_PATH, content);
                await ReloadAsync(systemctl, CancellationToken.None);
            }
            catch (Exception rollback)
            {
                exception.Data["RollbackError"] = rollback.Message;
            }
            throw;
        }
    }

    private static async Task UninstallAsync(string systemctl, CancellationToken cancellationToken)
    {
        if (!File.Exists(UNIT_PATH))
        {
            await ReloadAsync(systemctl, cancellationToken);
            if (await LoadStateAsync(systemctl, cancellationToken) != "not-found")
            {
                throw new WorkerServiceException("A systemd service with this name is registered outside the managed unit path.");
            }
            return;
        }
        var content = await WorkerServiceFiles.ReadManagedAsync(UNIT_PATH, cancellationToken);
        await WorkerServiceProcess.ExecuteAsync(systemctl, ["stop", SERVICE_UNIT], "stop the worker service", cancellationToken, 85);
        await WorkerServiceProcess.ExecuteAsync(systemctl, ["disable", SERVICE_UNIT], "disable automatic startup", cancellationToken);
        await WorkerServiceFiles.DeleteMatchingAsync(UNIT_PATH, content);
        try
        {
            await ReloadAsync(systemctl, cancellationToken);
        }
        catch (Exception exception)
        {
            try
            {
                if (!File.Exists(UNIT_PATH))
                {
                    await WorkerServiceFiles.WriteNewAsync(UNIT_PATH, content, CancellationToken.None);
                }
            }
            catch (Exception rollback)
            {
                exception.Data["RollbackError"] = rollback.Message;
            }
            throw;
        }
    }

    private static Task ReloadAsync(string systemctl, CancellationToken cancellationToken) =>
        WorkerServiceProcess.ExecuteAsync(systemctl, ["daemon-reload"], "reload systemd service definitions", cancellationToken);

    private static async Task<string> LoadStateAsync(string systemctl, CancellationToken cancellationToken)
    {
        var result = await WorkerServiceProcess.RunAsync(systemctl, ["show", "--property=LoadState", "--value", SERVICE_UNIT], cancellationToken);
        if (result.ExitCode == 4)
        {
            return "not-found";
        }
        WorkerServiceProcess.RequireSuccess(result, "query the installed systemd service");
        return result.Output.Trim();
    }
}
