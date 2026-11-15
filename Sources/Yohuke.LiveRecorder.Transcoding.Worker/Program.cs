using System.Globalization;
using System.Reflection;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;
using Yohuke.LiveRecorder.Transcoding.Worker.Hosting;
using Yohuke.LiveRecorder.Transcoding.Worker.Services;

namespace Yohuke.LiveRecorder.Transcoding.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var commandLine = WorkerCommandLine.Parse(args);
            if (commandLine.Command == WorkerCommand.Help)
            {
                WorkerCommandHelp.Write(commandLine.HelpCommand);
                return 0;
            }
            if (commandLine.Command == WorkerCommand.Version)
            {
                var assembly = typeof(Program).Assembly;
                var assemblyName = assembly.GetName();
                var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assemblyName.Version?.ToString() ?? "unknown";
                Console.WriteLine(assemblyName.Name + " " + version);
                return 0;
            }
            return await ExecuteAsync(commandLine);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Worker command failed: " + exception.Message);
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(WorkerCommandLine commandLine)
    {
        using var lifetime = new WorkerConsoleLifetime();
        try
        {
            switch (commandLine.Command)
            {
                case WorkerCommand.Register:
                    await WorkerApplication.RegisterAsync(commandLine.ConfigurationPath, lifetime.Token);
                    break;
                case WorkerCommand.Config:
                    await WorkerApplication.ConfigureAsync(commandLine.ConfigurationPath, lifetime.Token);
                    break;
                case WorkerCommand.Run:
                    await WorkerServiceHost.RunAsync(commandLine.ConfigurationPath, lifetime.Token);
                    break;
                case WorkerCommand.Status:
                case WorkerCommand.Log:
                    return await WorkerServiceInspection.ExecuteAsync(commandLine.Command, lifetime.Token);
                case WorkerCommand.Install:
                case WorkerCommand.Uninstall:
                case WorkerCommand.Start:
                case WorkerCommand.Stop:
                case WorkerCommand.Enable:
                case WorkerCommand.Disable:
                    await WorkerServices.ExecuteAsync(commandLine.Command, commandLine.ConfigurationPath, lifetime.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(commandLine));
            }
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.Token.IsCancellationRequested)
        {
            return 0;
        }
    }
}
