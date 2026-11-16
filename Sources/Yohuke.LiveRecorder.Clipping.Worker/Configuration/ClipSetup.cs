using Spectre.Console;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Clipping.Worker.Configuration;

internal static class ClipSetup
{
    internal static WorkerConfiguration Configure(WorkerConfiguration current)
    {
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new InvalidOperationException("Run 'register' or 'config' in an interactive terminal, or prepare settings.json.");
        }
        AnsiConsole.MarkupLine("[bold]Clipping worker configuration[/]");
        var result = current with
        {
            FfmpegPath = Text("FFmpeg path", current.FfmpegPath),
            FfprobePath = Text("ffprobe path", current.FfprobePath),
            TempDirectory = Path.GetFullPath(Text("Temporary directory", string.IsNullOrWhiteSpace(current.TempDirectory)
                ? Path.Combine(Path.GetTempPath(), "Yohuke.LiveRecorder") : current.TempDirectory)),
            MaxConcurrentEncodes = AnsiConsole.Prompt(new TextPrompt<int>("Concurrent clipping jobs")
                .DefaultValue(current.MaxConcurrentEncodes).Validate(value => value is >= 1 and <= 64)),
            MaxCacheBytes = Bytes("Temporary workspace limit (GiB)", current.MaxCacheBytes, 1024d * 1024 * 1024, 1d / 1024),
            MinimumFreeBytes = Bytes("Minimum free disk space (GiB)", current.MinimumFreeBytes, 1024d * 1024 * 1024, 0),
            DownloadBytesPerSecond = Bytes("Download limit (MiB/s, 0 for unlimited)", current.DownloadBytesPerSecond, 1024d * 1024, 0),
            UploadBytesPerSecond = Bytes("Upload limit (MiB/s, 0 for unlimited)", current.UploadBytesPerSecond, 1024d * 1024, 0)
        };
        WorkerConfigurationValidation.Validate(result, "Clipping");
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
