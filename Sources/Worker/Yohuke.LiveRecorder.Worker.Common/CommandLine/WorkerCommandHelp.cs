namespace Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;

internal static class WorkerCommandHelp
{
    internal static void Write(WorkerCommand? command, string executableName, string title, string kind = "Transcoding")
    {
        var configuration = kind switch
        {
            "Transcoding" => "Edit transcoding tools, CPU/GPU encoding, storage and transfer settings.",
            "Clipping" => "Edit clipping tools, job concurrency, Exact CPU threads, storage and transfer settings.",
            "Recording" => "Edit concurrent recording rooms, persistent spool, disk reserve and upload settings.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (command is null)
        {
            Console.WriteLine($"""
                {title}

                Usage
                  {executableName} [--config <path>] <command>

                Commands
                  install     Install a stopped system service with manual startup.
                  uninstall   Stop and remove the system service. Keep the worker settings.
                  register    Bind this worker to a server and synchronize its settings.
                  config      {configuration}
                  start       Start the installed system service.
                  stop        Stop the system service and its media processes.
                  status      Show installation, runtime, startup and process information.
                  log         Show the latest 100 service log entries and follow new output.
                  enable      Enable automatic startup at boot.
                  disable     Disable automatic startup at boot.
                  run         Run the worker in the foreground. Use Ctrl+C to stop.
                  help        Show help. Use 'help <command>' for command details.
                  version     Show the worker version.

                Options
                  --config <path>    Settings file. Defaults to settings.json beside the executable.
                  -h, --help         Show help for the selected command.

                System service management requires administrator privileges.
                """);
            return;
        }
        var description = command switch
        {
            WorkerCommand.Register => "Bind to a server using its HTTP or HTTPS address, TCP control endpoint, worker ID and secret.\nThe TCP control host defaults to the host in the server URL. Configure control TLS in the registration prompt.\nConnection settings are saved before connecting. Runtime settings are synchronized with the server.",
            WorkerCommand.Config => configuration + "\nA registered worker reads the latest server settings and synchronizes the changes.\nAn unregistered worker saves its runtime settings locally for the next 'register'.\nStop the worker before editing its settings.",
            WorkerCommand.Run => "Run using the saved binding and runtime settings. Network interruptions are retried.\nRun 'register' first. Use Ctrl+C or SIGTERM to stop the worker and its media processes.",
            WorkerCommand.Help => "Show general help or the help for one command.",
            WorkerCommand.Version => "Show the version embedded in this worker build.",
            WorkerCommand.Install => "Install a system service using this executable and the absolute settings file path.\nThe service is initially stopped with manual startup. Run 'enable' to start it at boot.\nNo server connection is required. Administrator privileges are required.",
            WorkerCommand.Uninstall => "Stop the system service, wait for media processes to exit and remove the service registration.\nThe worker settings and cached source files are preserved. Administrator privileges are required.",
            WorkerCommand.Start => "Start the installed system service using its registered executable and settings file.\nRun 'register' before starting an unconfigured worker. Administrator privileges are required.",
            WorkerCommand.Stop => "Stop the installed system service and wait for its media processes to exit.\nThe automatic startup setting is preserved. Administrator privileges are required.",
            WorkerCommand.Status => "Show the installed service state, automatic startup mode, process ID and log source.\nExit code is 0 when running and 1 otherwise.\nUses the installed service registration. On macOS, use an administrator terminal for an installed system service.",
            WorkerCommand.Log => "Show recent service logs and follow new output until Ctrl+C.\nUses the installed service registration and the operating system log permissions.\nOn macOS, read the latest 100 lines from each of worker.log and worker-error.log.\nOn Linux, read the latest 100 journal entries. On Windows, read the latest 100 worker events in the Application log.",
            WorkerCommand.Enable => "Enable automatic startup of the installed system service at boot.\nUse 'start' to run it immediately. Administrator privileges are required.",
            WorkerCommand.Disable => "Disable automatic startup of the installed system service at boot.\nUse 'stop' to stop a running service. Administrator privileges are required.",
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        Console.WriteLine($"Usage\n  {executableName} {command.ToString()!.ToLowerInvariant()} [--config <path>]\n\n{description}");
    }
}
