using Spectre.Console;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Recording.Worker.Configuration;

internal static class RecordingSetup
{
    internal static WorkerConfiguration Configure(WorkerConfiguration current) =>
        WorkerConfigurationProjection.ToLegacy(Configure(WorkerConfigurationProjection.ToRecording(current)));

    private static RecordingWorkerConfiguration Configure(RecordingWorkerConfiguration current)
    {
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new InvalidOperationException("Run 'register' or 'config' in an interactive terminal, or prepare settings.json.");
        }
        var fallback = string.IsNullOrWhiteSpace(current.TempDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yohuke.LiveRecorder", "RecordingSpool")
            : current.TempDirectory;
        var result = current with
        {
            TempDirectory = Path.GetFullPath(AnsiConsole.Prompt(new TextPrompt<string>("Persistent recording spool directory")
                .DefaultValue(fallback).Validate(value => !string.IsNullOrWhiteSpace(value)))),
            MaxConcurrentEncodes = AnsiConsole.Prompt(new TextPrompt<int>("Concurrent recording rooms")
                .DefaultValue(current.MaxConcurrentEncodes).Validate(value => value is >= 1 and <= 64)),
            MaxCacheBytes = Bytes("Persistent spool limit (GiB)", current.MaxCacheBytes, 1024d * 1024 * 1024, 1d / 1024),
            MinimumFreeBytes = Bytes("Minimum free disk space (GiB)", current.MinimumFreeBytes, 1024d * 1024 * 1024, 0),
            UploadBytesPerSecond = Bytes("Upload limit (MiB/s, 0 for unlimited)", current.UploadBytesPerSecond, 1024d * 1024, 0)
        };
        WorkerConfigurationValidation.Validate(WorkerConfigurationProjection.ToLegacy(result), "Recording");
        return result;
    }

    private static long Bytes(string title, long current, double unit, double minimum)
    {
        var value = AnsiConsole.Prompt(new TextPrompt<double>(title).DefaultValue(current / unit)
            .Validate(value => double.IsFinite(value) && value >= minimum && value < long.MaxValue / unit));
        return checked((long)(value * unit));
    }
}
