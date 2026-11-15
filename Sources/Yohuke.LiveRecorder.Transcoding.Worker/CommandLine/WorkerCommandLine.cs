namespace Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;

internal sealed record WorkerCommandLine
{
    internal WorkerCommand Command { get; init; }
    internal WorkerCommand? HelpCommand { get; init; }
    internal string ConfigurationPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "settings.json");

    internal static WorkerCommandLine Parse(IReadOnlyList<string> args)
    {
        var arguments = new List<string>();
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        var configurationSpecified = false;
        var helpRequested = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--config":
                    if (configurationSpecified || index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1])
                        || args[index + 1].StartsWith('-'))
                    {
                        throw new ArgumentException("Specify --config once, followed by a settings file path.");
                    }
                    configurationPath = Path.GetFullPath(args[++index]);
                    configurationSpecified = true;
                    break;
                case "--help":
                case "-h":
                    helpRequested = true;
                    break;
                default:
                    if (args[index].StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option '{args[index]}'. Run 'help' for usage.");
                    }
                    arguments.Add(args[index]);
                    break;
            }
        }
        var command = arguments.Count == 0 ? WorkerCommand.Help : ParseCommand(arguments[0]);
        WorkerCommand? helpCommand = null;
        if (command == WorkerCommand.Help && arguments.Count == 2)
        {
            helpCommand = ParseCommand(arguments[1]);
        }
        else if (arguments.Count > 1)
        {
            throw new ArgumentException("Specify one command at a time. Run 'help' for usage.");
        }
        if (helpRequested && command != WorkerCommand.Help)
        {
            helpCommand = command;
            command = WorkerCommand.Help;
        }
        return new()
        {
            Command = command,
            HelpCommand = helpCommand,
            ConfigurationPath = configurationPath
        };
    }

    private static WorkerCommand ParseCommand(string command) => command switch
    {
        "help" => WorkerCommand.Help,
        "version" => WorkerCommand.Version,
        "register" => WorkerCommand.Register,
        "config" => WorkerCommand.Config,
        "run" => WorkerCommand.Run,
        "install" => WorkerCommand.Install,
        "uninstall" => WorkerCommand.Uninstall,
        "start" => WorkerCommand.Start,
        "stop" => WorkerCommand.Stop,
        "status" => WorkerCommand.Status,
        "log" => WorkerCommand.Log,
        "enable" => WorkerCommand.Enable,
        "disable" => WorkerCommand.Disable,
        _ => throw new ArgumentException($"Unknown command '{command}'. Run 'help' for usage.")
    };
}
