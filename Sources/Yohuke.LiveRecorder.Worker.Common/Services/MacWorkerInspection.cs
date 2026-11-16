using System.Globalization;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed class MacWorkerInspection(WorkerServiceIdentity identity)
{
    private WorkerServiceFiles Files { get; } = new(identity);

    private string LogSource => Path.Combine(identity.MacDefinitionDirectory, "worker.log")
        + ", " + Path.Combine(identity.MacDefinitionDirectory, "worker-error.log");

    private IReadOnlyDictionary<string, string> ToolEnvironment { get; } = new Dictionary<string, string>
    {
        ["LC_ALL"] = "C"
    };

    internal IReadOnlyList<string> LogArguments { get; } =
    [
        "-n", WorkerServiceInspection.RECENT_LOG_COUNT.ToString(CultureInfo.InvariantCulture), "-F",
        Path.Combine(identity.MacDefinitionDirectory, "worker.log"),
        Path.Combine(identity.MacDefinitionDirectory, "worker-error.log")
    ];

    internal async Task<WorkerServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var definition = await Files.ReadOptionalManagedAsync(identity.MacDefinitionPath, cancellationToken);
        if (definition is null)
        {
            return new() { LogSource = LogSource };
        }
        WorkerServices.RequireAdministrator();
        var state = await new MacWorkerService(identity).QueryAsync(cancellationToken);
        var autostart = await Files.ReadOptionalManagedAsync(identity.MacAutostartPath, cancellationToken);
        if (autostart is not null && autostart != definition)
        {
            throw new WorkerServiceException("The automatic startup definition belongs to a different worker installation.");
        }
        var disabled = await WorkerServiceProcess.RunAsync("/bin/launchctl", ["print-disabled", "system"],
            cancellationToken, environment: ToolEnvironment);
        WorkerServiceProcess.RequireSuccess(disabled, "query the worker service startup policy");
        return CreateStatus(state, autostart is not null, ParseDisabledState(disabled.Output));
    }

    internal WorkerServiceStatus CreateStatus(MacWorkerServiceState? state, bool automaticStartup, bool disabled = false) => new()
    {
        Installed = true,
        IsRunning = state?.ProcessId is > 0,
        State = state is null ? "Stopped" : state.ProcessId is > 0 ? "Running" : "Waiting",
        Startup = disabled ? "Disabled" : automaticStartup ? "Automatic" : "Manual",
        ProcessId = state?.ProcessId,
        LogSource = LogSource
    };

    internal bool ParseDisabledState(string output)
    {
        var content = output.Trim();
        if (!content.StartsWith("disabled services = {", StringComparison.Ordinal) || !content.EndsWith('}'))
        {
            throw new WorkerServiceException("launchctl returned an incomplete service startup policy.");
        }
        var prefix = "\"" + identity.LaunchdLabel + "\" => ";
        foreach (var line in content.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry[prefix.Length..].Trim() switch
                {
                    "disabled" or "true" => true,
                    "enabled" or "false" => false,
                    _ => throw new WorkerServiceException("launchctl returned an unknown worker service startup policy.")
                };
            }
        }
        return false;
    }

    internal async Task FollowLogAsync(CancellationToken cancellationToken)
    {
        await Files.ReadManagedAsync(identity.MacDefinitionPath, cancellationToken);
        Console.WriteLine("Following worker service logs. Press Ctrl+C to stop.");
        var result = await new WorkerProcessRunner().RunAsync("/usr/bin/tail", LogArguments, cancellationToken,
            outputLine: Console.WriteLine, environment: ToolEnvironment, errorLine: Console.Error.WriteLine);
        WorkerServiceProcess.RequireSuccess(result, "follow the worker service logs");
    }
}
