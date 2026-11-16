using System.Globalization;
using System.Reflection;
using Yohuke.LiveRecorder.Clipping.Worker.Hosting;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;
using Yohuke.LiveRecorder.Transcoding.Worker.Hosting;
using Yohuke.LiveRecorder.Transcoding.Worker.Services;

namespace Yohuke.LiveRecorder.Clipping.Worker;

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
                WorkerCommandHelp.Write(commandLine.HelpCommand, "ylr-clip-worker", "Clipping worker", "Clipping");
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
                        await ClipApplication.RegisterAsync(commandLine.ConfigurationPath, lifetime.Token);
                        break;
                    case WorkerCommand.Config:
                        await ClipApplication.ConfigureAsync(commandLine.ConfigurationPath, lifetime.Token);
                        break;
                    case WorkerCommand.Run:
                        await WorkerServiceRunner.RunAsync(commandLine.ConfigurationPath, WorkerServiceIdentity.Clipping,
                            ClipApplication.RunAsync, lifetime.Token);
                        break;
                    case WorkerCommand.Status:
                    case WorkerCommand.Log:
                        return await WorkerServiceInspection.ExecuteAsync(commandLine.Command, WorkerServiceIdentity.Clipping, lifetime.Token);
                    default:
                        await WorkerServices.ExecuteAsync(commandLine.Command, commandLine.ConfigurationPath,
                            WorkerServiceIdentity.Clipping, lifetime.Token);
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
            Console.Error.WriteLine("Clipping worker command failed: " + error.Message);
            return 1;
        }
    }
}
