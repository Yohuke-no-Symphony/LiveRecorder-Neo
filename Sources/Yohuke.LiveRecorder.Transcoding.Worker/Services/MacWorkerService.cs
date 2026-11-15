using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal static class MacWorkerService
{
    internal const string DEFINITION_DIRECTORY = "/Library/Application Support/Yohuke.LiveRecorder/WorkerService";
    internal const string DEFINITION_PATH = DEFINITION_DIRECTORY + "/" + WorkerServices.LAUNCHD_LABEL + ".plist";
    internal const string AUTOSTART_PATH = "/Library/LaunchDaemons/" + WorkerServices.LAUNCHD_LABEL + ".plist";
    private const string SERVICE_TARGET = "system/" + WorkerServices.LAUNCHD_LABEL;
    private const string LAUNCHCTL = "/bin/launchctl";

    internal static async Task ExecuteAsync(WorkerCommand command, string configurationPath, CancellationToken cancellationToken)
    {
        if (command == WorkerCommand.Install)
        {
            await InstallAsync(WorkerServiceInvocation.Current(configurationPath), cancellationToken);
            return;
        }
        if (command == WorkerCommand.Uninstall)
        {
            await UninstallAsync(cancellationToken);
            return;
        }
        var definition = await WorkerServiceFiles.ReadManagedAsync(DEFINITION_PATH, cancellationToken);
        switch (command)
        {
            case WorkerCommand.Start:
                await StartAsync(cancellationToken);
                break;
            case WorkerCommand.Stop:
                await StopAsync(cancellationToken);
                break;
            case WorkerCommand.Enable:
                await EnableAsync(definition, cancellationToken);
                break;
            case WorkerCommand.Disable:
                await RequireMatchingAutostartAsync(definition, cancellationToken);
                await WorkerServiceFiles.DeleteMatchingAsync(AUTOSTART_PATH, definition);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    internal static string CreatePlist(WorkerServiceInvocation invocation)
    {
        var document = new XDocument(new XComment(WorkerServiceFiles.OWNER_MARKER),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", WorkerServices.LAUNCHD_LABEL),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array", invocation.Arguments.Prepend(invocation.ExecutablePath).Select(value => new XElement("string", value))),
                    new XElement("key", "WorkingDirectory"), new XElement("string", invocation.WorkingDirectory),
                    new XElement("key", "UserName"), new XElement("string", "root"),
                    new XElement("key", "RunAtLoad"), new XElement("true"),
                    new XElement("key", "KeepAlive"), new XElement("true"),
                    new XElement("key", "ThrottleInterval"), new XElement("integer", 10),
                    new XElement("key", "ExitTimeOut"), new XElement("integer", 75),
                    new XElement("key", "EnvironmentVariables"),
                    new XElement("dict",
                        new XElement("key", "PATH"), new XElement("string", "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"),
                        new XElement("key", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"), new XElement("string", DEFINITION_DIRECTORY + "/bundles")),
                    new XElement("key", "StandardOutPath"), new XElement("string", DEFINITION_DIRECTORY + "/worker.log"),
                    new XElement("key", "StandardErrorPath"), new XElement("string", DEFINITION_DIRECTORY + "/worker-error.log"))));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n{document}\n";
    }

    internal static MacWorkerServiceState? ParseState(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SequenceEqual(new[] { "PID", "Status", "Label" }))
        {
            throw new WorkerServiceException("Unexpected launchctl list output. The worker service registration was preserved.");
        }
        var rows = lines.Skip(1).Select(line => line.Split((char[]?)null, 3,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        if (rows.Any(row => row.Length != 3))
        {
            throw new WorkerServiceException("Unexpected launchctl list output. The worker service registration was preserved.");
        }
        var service = rows.SingleOrDefault(row => row[2] == WorkerServices.LAUNCHD_LABEL);
        if (service is null)
        {
            return null;
        }
        if (service[0] == "-")
        {
            return new(null);
        }
        if (!int.TryParse(service[0], CultureInfo.InvariantCulture, out var processId) || processId <= 0)
        {
            throw new WorkerServiceException("launchctl reported an invalid worker process identifier. The service registration was preserved.");
        }
        return new(processId);
    }

    private static async Task InstallAsync(WorkerServiceInvocation invocation, CancellationToken cancellationToken)
    {
        var content = CreatePlist(invocation);
        WorkerServiceFiles.CreatePrivateDirectory(Path.Combine(DEFINITION_DIRECTORY, "bundles"));
        if (File.Exists(DEFINITION_PATH))
        {
            if (await WorkerServiceFiles.ReadManagedAsync(DEFINITION_PATH, cancellationToken) != content)
            {
                throw new WorkerServiceException("The worker service is already installed with a different executable or configuration. Run 'uninstall' before installing it again.");
            }
            await RequireMatchingAutostartAsync(content, cancellationToken);
            return;
        }
        if (File.Exists(AUTOSTART_PATH) || await QueryAsync(cancellationToken) is not null)
        {
            throw new WorkerServiceException("A launchd service with this name is already registered by another installation.");
        }
        await WorkerServiceFiles.WriteNewAsync(DEFINITION_PATH, content, cancellationToken);
    }

    private static async Task StartAsync(CancellationToken cancellationToken)
    {
        await WorkerServiceProcess.ExecuteAsync(LAUNCHCTL, ["enable", SERVICE_TARGET], "allow the worker service to start", cancellationToken);
        if (await QueryAsync(cancellationToken) is null)
        {
            await WorkerServiceProcess.ExecuteAsync(LAUNCHCTL, ["bootstrap", "system", DEFINITION_PATH],
                "load the worker service", cancellationToken);
        }
        var started = await WorkerServiceProcess.RunAsync(LAUNCHCTL, ["kickstart", "-p", SERVICE_TARGET], cancellationToken);
        WorkerServiceProcess.RequireSuccess(started, "start the worker service");
        if (!int.TryParse(started.Output.Trim(), CultureInfo.InvariantCulture, out var processId) || processId <= 0)
        {
            throw new WorkerServiceException("launchd did not report a running worker process.");
        }
        using var process = FindProcess(processId);
        if (process is null || process.HasExited)
        {
            throw new WorkerServiceException("The worker service exited during startup. Check the worker service logs.");
        }
    }

    private static async Task StopAsync(CancellationToken cancellationToken)
    {
        var state = await QueryAsync(cancellationToken);
        if (state is null)
        {
            return;
        }
        using var process = FindProcess(state.ProcessId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(85));
        try
        {
            var removed = await WorkerServiceProcess.RunAsync(LAUNCHCTL, ["bootout", SERVICE_TARGET], timeout.Token, 85);
            if (removed.ExitCode != 113)
            {
                WorkerServiceProcess.RequireSuccess(removed, "stop and unload the worker service");
            }
            if (process is not null)
            {
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkerServiceException("Timed out waiting for the worker service to stop.", exception);
        }
    }

    private static Process? FindProcess(int? processId)
    {
        if (processId is null)
        {
            return null;
        }
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId.Value);
            _ = process.Handle;
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            || exception is System.ComponentModel.Win32Exception { NativeErrorCode: 3 })
        {
            process?.Dispose();
            return null;
        }
    }

    private static async Task EnableAsync(string definition, CancellationToken cancellationToken)
    {
        await RequireMatchingAutostartAsync(definition, cancellationToken);
        var created = !File.Exists(AUTOSTART_PATH);
        if (created)
        {
            await WorkerServiceFiles.WriteNewAsync(AUTOSTART_PATH, definition, cancellationToken);
        }
        try
        {
            await WorkerServiceProcess.ExecuteAsync(LAUNCHCTL, ["enable", SERVICE_TARGET], "enable automatic startup", cancellationToken);
        }
        catch (Exception exception)
        {
            if (created)
            {
                try
                {
                    await WorkerServiceFiles.DeleteMatchingAsync(AUTOSTART_PATH, definition);
                }
                catch (Exception rollback)
                {
                    exception.Data["RollbackError"] = rollback.Message;
                }
            }
            throw;
        }
    }

    private static async Task UninstallAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DEFINITION_PATH))
        {
            if (File.Exists(AUTOSTART_PATH) || await QueryAsync(cancellationToken) is not null)
            {
                throw new WorkerServiceException("A launchd service with this name exists without a managed service definition.");
            }
            return;
        }
        var definition = await WorkerServiceFiles.ReadManagedAsync(DEFINITION_PATH, cancellationToken);
        await RequireMatchingAutostartAsync(definition, cancellationToken);
        await StopAsync(cancellationToken);
        await WorkerServiceFiles.DeleteMatchingAsync(AUTOSTART_PATH, definition);
        await WorkerServiceFiles.DeleteMatchingAsync(DEFINITION_PATH, definition);
    }

    private static async Task RequireMatchingAutostartAsync(string definition, CancellationToken cancellationToken)
    {
        if (File.Exists(AUTOSTART_PATH) && await WorkerServiceFiles.ReadManagedAsync(AUTOSTART_PATH, cancellationToken) != definition)
        {
            throw new WorkerServiceException("The automatic startup definition belongs to a different worker installation.");
        }
    }

    internal static async Task<MacWorkerServiceState?> QueryAsync(CancellationToken cancellationToken)
    {
        var result = await WorkerServiceProcess.RunAsync(LAUNCHCTL, ["list"], cancellationToken);
        WorkerServiceProcess.RequireSuccess(result, "query the installed launchd service");
        return ParseState(result.Output);
    }
}
