using System.Globalization;
using System.Reflection;
using Yohuke.LiveRecorder.AI.Worker.Hosting;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;
using Yohuke.LiveRecorder.Transcoding.Worker.Hosting;
using Yohuke.LiveRecorder.Transcoding.Worker.Services;

namespace Yohuke.LiveRecorder.AI.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var command = WorkerCommandLine.Parse(args);
            if (command.Command == WorkerCommand.Help)
            {
                WorkerCommandHelp.Write(command.HelpCommand, "ylr-ai-worker", "AI worker", "AI");
                return 0;
            }
            if (command.Command == WorkerCommand.Version)
            {
                var assembly = typeof(Program).Assembly;
                Console.WriteLine(assembly.GetName().Name + " " + (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assembly.GetName().Version?.ToString() ?? "unknown"));
                return 0;
            }
            using var lifetime = new WorkerConsoleLifetime();
            try
            {
                switch (command.Command)
                {
                    case WorkerCommand.Register:
                        await AiApplication.RegisterAsync(command.ConfigurationPath, lifetime.Token);
                        break;
                    case WorkerCommand.Config:
                        await AiApplication.ConfigureAsync(command.ConfigurationPath, lifetime.Token);
                        break;
                    case WorkerCommand.Run:
                        await WorkerServiceRunner.RunAsync(command.ConfigurationPath, WorkerServiceIdentity.Ai, AiApplication.RunAsync, lifetime.Token);
                        break;
                    case WorkerCommand.Status:
                    case WorkerCommand.Log:
                        return await WorkerServiceInspection.ExecuteAsync(command.Command, WorkerServiceIdentity.Ai, lifetime.Token);
                    default:
                        await WorkerServices.ExecuteAsync(command.Command, command.ConfigurationPath, WorkerServiceIdentity.Ai, lifetime.Token);
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
            Console.Error.WriteLine("AI worker command failed: " + error.Message);
            return 1;
        }
    }
}
