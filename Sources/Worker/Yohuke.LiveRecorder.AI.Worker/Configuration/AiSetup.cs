using Spectre.Console;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.AI.Worker.Configuration;

internal static class AiSetup
{
    internal static WorkerConfiguration Configure(WorkerConfiguration current) =>
        WorkerConfigurationProjection.ToLegacy(Configure(WorkerConfigurationProjection.ToAi(current)));

    private static AiWorkerConfiguration Configure(AiWorkerConfiguration current)
    {
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new InvalidOperationException("Run 'register' or 'config' in an interactive terminal, or prepare settings.json.");
        }
        AnsiConsole.MarkupLine("[bold]AI worker configuration[/]");
        var result = current with
        {
            TempDirectory = Path.GetFullPath(Text("Temporary directory", string.IsNullOrWhiteSpace(current.TempDirectory)
                ? Path.Combine(Path.GetTempPath(), "Yohuke.LiveRecorder") : current.TempDirectory)),
            MaxConcurrentJobs = AnsiConsole.Prompt(new TextPrompt<int>("Concurrent AI jobs")
                .DefaultValue(current.MaxConcurrentJobs).Validate(value => value is >= 1 and <= 64)),
            MaxCacheBytes = Bytes("Temporary workspace limit (GiB)", current.MaxCacheBytes, 1024d * 1024 * 1024, 1d / 1024),
            MinimumFreeBytes = Bytes("Minimum free disk space (GiB)", current.MinimumFreeBytes, 1024d * 1024 * 1024, 0),
            DownloadBytesPerSecond = Bytes("Download limit (MiB/s, 0 for unlimited)", current.DownloadBytesPerSecond, 1024d * 1024, 0),
            UploadBytesPerSecond = Bytes("Upload limit (MiB/s, 0 for unlimited)", current.UploadBytesPerSecond, 1024d * 1024, 0)
        };
        WorkerConfigurationValidation.Validate(WorkerConfigurationProjection.ToLegacy(result), "AI");
        return result;
    }

    private static string Text(string label, string current) => AnsiConsole.Prompt(new TextPrompt<string>(label)
        .DefaultValue(current).Validate(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static long Bytes(string label, long current, double unit, double minimum)
    {
        var value = AnsiConsole.Prompt(new TextPrompt<double>(label).DefaultValue(current / unit)
            .Validate(input => double.IsFinite(input) && input >= minimum && input <= long.MaxValue / unit));
        return checked((long)(value * unit));
    }
}
