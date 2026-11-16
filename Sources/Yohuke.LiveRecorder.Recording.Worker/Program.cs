using System.Globalization;
using System.Reflection;
using Yohuke.LiveRecorder.Recording.Worker.Hosting;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;
using Yohuke.LiveRecorder.Transcoding.Worker.Hosting;
using Yohuke.LiveRecorder.Transcoding.Worker.Services;

namespace Yohuke.LiveRecorder.Recording.Worker;

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
                WorkerCommandHelp.Write(commandLine.HelpCommand, "ylr-rec-worker", "Recording worker");
                return 0;
            }
            if (commandLine.Command == WorkerCommand.Version)
            {
                var assembly = typeof(Program).Assembly;
                Console.WriteLine(assembly.GetName().Name + " " + (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assembly.GetName().Version?.ToString() ?? "unknown"));
                return 0;
            }
            using var lifetime = new WorkerConsoleLifetime();
            try
            {
                switch (commandLine.Command)
                {
                    case WorkerCommand.Register:
                        await RecordingApplication.RegisterAsync(commandLine.ConfigurationPath, lifetime.Token);
                        break;
                    case WorkerCommand.Config:
                        await RecordingApplication.ConfigureAsync(commandLine.ConfigurationPath, lifetime.Token);
                        break;
                    case WorkerCommand.Run:
                        await WorkerServiceRunner.RunAsync(commandLine.ConfigurationPath, WorkerServiceIdentity.Recording,
                            RecordingApplication.RunAsync, lifetime.Token);
                        break;
                    case WorkerCommand.Status:
                    case WorkerCommand.Log:
                        return await WorkerServiceInspection.ExecuteAsync(commandLine.Command, WorkerServiceIdentity.Recording, lifetime.Token);
                    default:
                        await WorkerServices.ExecuteAsync(commandLine.Command, commandLine.ConfigurationPath,
                            WorkerServiceIdentity.Recording, lifetime.Token);
                        break;
                }
                return 0;
            }
            catch (OperationCanceledException) when (lifetime.Token.IsCancellationRequested)
            {
                return 0;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Recording worker command failed: " + error.Message);
            return 1;
        }
    }
}
