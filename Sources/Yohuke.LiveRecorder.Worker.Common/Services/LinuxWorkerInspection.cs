using System.ComponentModel;
using System.Globalization;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed class LinuxWorkerInspection(WorkerServiceIdentity identity)
{
    private WorkerServiceFiles Files { get; } = new(identity);

    private string LogSource => "journalctl --unit=" + identity.LinuxUnit;

    private IReadOnlyDictionary<string, string> ToolEnvironment { get; } = new Dictionary<string, string>
    {
        ["LC_ALL"] = "C"
    };

    internal IReadOnlyList<string> LogArguments { get; } =
    [
        "--unit=" + identity.LinuxUnit,
        "--lines=" + WorkerServiceInspection.RECENT_LOG_COUNT.ToString(CultureInfo.InvariantCulture),
        "--follow",
        "--no-pager",
        "--output=short-iso"
    ];

    internal async Task<WorkerServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await Files.ReadOptionalManagedAsync(identity.LinuxUnitPath, cancellationToken) is null)
        {
            return new() { LogSource = LogSource };
        }
        var result = await WorkerServiceProcess.RunAsync(LinuxWorkerService.GetSystemctlPath(),
            ["show", "--all", "--property=LoadState,ActiveState,SubState,MainPID,UnitFileState", identity.LinuxUnit],
            cancellationToken, environment: ToolEnvironment);
        return ParseStatus(result, true);
    }

    internal WorkerServiceStatus ParseStatus(WorkerProcessResult result, bool definitionPresent = false)
    {
        if (result.ExitCode is not (0 or 4))
        {
            WorkerServiceProcess.RequireSuccess(result, "query the worker service status");
        }
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                properties[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        var loadState = properties.GetValueOrDefault("LoadState");
        if (loadState == "not-found")
        {
            return new()
            {
                Installed = definitionPresent,
                State = definitionPresent ? "Not loaded" : "Not installed",
                LogSource = LogSource
            };
        }
        WorkerServiceProcess.RequireSuccess(result, "query the worker service status");
        var activeState = properties.GetValueOrDefault("ActiveState");
        var subState = properties.GetValueOrDefault("SubState");
        if (string.IsNullOrWhiteSpace(loadState) || string.IsNullOrWhiteSpace(activeState) || string.IsNullOrWhiteSpace(subState))
        {
            var failure = new WorkerServiceException("systemd returned an incomplete worker service status.");
            failure.Data["StandardOutput"] = result.Output;
            failure.Data["StandardError"] = result.Diagnostic;
            throw failure;
        }
        var state = (activeState, subState, loadState) switch
        {
            ("active", "running", _) => "Running",
            ("reloading", _, _) => "Reloading",
            ("activating", "auto-restart" or "auto-restart-queued", _) => "Restarting",
            ("activating", _, _) => "Starting",
            ("deactivating", _, _) => "Stopping",
            (_, _, "masked") => "Masked",
            (_, _, "error" or "bad-setting") => "Load error",
            (_, _, "stub") => "Not loaded",
            ("active", "exited", _) => "Exited",
            ("active", _, _) => "Active",
            ("inactive", _, _) => "Stopped",
            ("failed", _, _) => "Failed",
            _ => "Unavailable"
        };
        var startup = properties.GetValueOrDefault("UnitFileState") switch
        {
            "enabled" => "Enabled",
            "enabled-runtime" => "Enabled (runtime)",
            "disabled" => "Disabled",
            "masked" => "Disabled (masked)",
            "masked-runtime" => "Disabled (runtime mask)",
            "static" => "Static",
            "indirect" => "Indirect",
            "linked" => "Linked",
            "linked-runtime" => "Linked (runtime)",
            "generated" => "Generated",
            "transient" => "Transient",
            "alias" => "Alias",
            _ => "Unavailable"
        };
        var processId = int.TryParse(properties.GetValueOrDefault("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId)
            && parsedId > 0 ? parsedId : (int?)null;
        return new()
        {
            Installed = true,
            IsRunning = state is "Running" or "Reloading",
            State = state,
            Startup = startup,
            ProcessId = processId,
            LogSource = LogSource
        };
    }

    internal async Task FollowLogAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Installed)
        {
            throw new WorkerServiceException("The worker service is not installed. Run 'install' first.");
        }
        var journalctl = File.Exists("/usr/bin/journalctl") ? "/usr/bin/journalctl" : "/bin/journalctl";
        if (!File.Exists(journalctl))
        {
            throw new WorkerServiceException("Reading Linux worker service logs requires journalctl.");
        }
        try
        {
            Console.WriteLine("Following worker service logs. Press Ctrl+C to stop.");
            var result = await new WorkerProcessRunner().RunAsync(journalctl, LogArguments, cancellationToken,
                outputLine: Console.WriteLine, environment: ToolEnvironment, errorLine: Console.Error.WriteLine);
            WorkerServiceProcess.RequireSuccess(result, "read the worker service journal (check journal access permissions)");
        }
        catch (Win32Exception exception)
        {
            var failure = new WorkerServiceException("Unable to execute journalctl to read the worker service logs.", exception);
            failure.Data["NativeMessage"] = exception.Message;
            failure.Data["NativeErrorCode"] = exception.NativeErrorCode;
            throw failure;
        }
    }
}
