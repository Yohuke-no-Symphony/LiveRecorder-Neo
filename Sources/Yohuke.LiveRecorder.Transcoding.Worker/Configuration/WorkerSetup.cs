using Spectre.Console;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerSetup
{
    internal static WorkerConnectionOptions ConfigureConnection(WorkerConnectionOptions current) =>
        WorkerConnectionSetup.Configure(current, "Transcoding worker registration");

    internal static WorkerConfiguration Configure(WorkerConfiguration current)
    {
        RequireInteractive();
        AnsiConsole.MarkupLine("[bold]Worker runtime settings[/]");
        var result = current with
        {
            FfmpegPath = Text("FFmpeg path", current.FfmpegPath),
            FfprobePath = Text("ffprobe path", current.FfprobePath),
            TempDirectory = Text("Local temporary directory", string.IsNullOrWhiteSpace(current.TempDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yohuke.LiveRecorder", "Worker")
                : current.TempDirectory),
            MaxConcurrentEncodes = Integer("Total concurrent encodes (GPU + CPU)", current.MaxConcurrentEncodes, 1, 64),
            GpuConcurrency = Integer("Concurrent GPU encodes", current.GpuConcurrency, 0, 64),
            CpuConcurrency = Integer("Concurrent CPU fallback encodes", current.CpuConcurrency, 0, 64),
            CpuFallbackMode = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("CPU fallback policy")
                .AddChoices(new[] { current.CpuFallbackMode, "Balanced", "Disabled" }.Distinct())),
            PreferredEncoder = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Preferred encoder")
                .AddChoices(new[] { current.PreferredEncoder, "auto" }.Concat(EncoderArguments.ENCODERS.Where(value => value != "libx264")).Distinct())),
            DeviceId = ConnectionText("Device ID (automatic when unset)", current.DeviceId),
            MaxCacheBytes = (long)(Number("Temporary cache limit (GiB)", current.MaxCacheBytes / (1024d * 1024 * 1024), 1d / 1024, 1048576) * 1024 * 1024 * 1024),
            MinimumFreeBytes = (long)(Number("Minimum free disk space (GiB)", current.MinimumFreeBytes / (1024d * 1024 * 1024), 0, 1048576) * 1024 * 1024 * 1024)
        };
        if (AnsiConsole.Confirm("Configure CPU fallback thresholds and transfer limits?", false))
        {
            result.GpuWaitSeconds = Integer("Wait for GPU before CPU fallback (seconds)", current.GpuWaitSeconds, 0, 86400);
            result.CpuSampleWindowSeconds = Integer("CPU sampling window (seconds)", current.CpuSampleWindowSeconds, 5, 300);
            result.CpuLoadThreshold = Number("CPU load threshold for fallback (%)", current.CpuLoadThreshold, 1, 100);
            result.CpuThreadFraction = Number("CPU encoding thread budget fraction (default 0.5)", current.CpuThreadFraction, 0.01, 1);
            result.DownloadBytesPerSecond = (long)(Number("Download limit (MiB/s, 0 for unlimited)", current.DownloadBytesPerSecond / (1024d * 1024), 0, 1048576) * 1024 * 1024);
            result.UploadBytesPerSecond = (long)(Number("Upload limit (MiB/s, 0 for unlimited)", current.UploadBytesPerSecond / (1024d * 1024), 0, 1048576) * 1024 * 1024);
        }
        result.TempDirectory = Path.GetFullPath(result.TempDirectory);
        result.DeviceId = string.IsNullOrWhiteSpace(result.DeviceId) ? null : result.DeviceId;
        return result;
    }

    internal static void Validate(WorkerConfiguration configuration) => WorkerConfigurationValidation.Validate(configuration);

    private static void RequireInteractive()
    {
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new InvalidOperationException("This command requires an interactive terminal. Run 'register' or 'config' interactively, or prepare the settings file selected by --config.");
        }
    }

    private static string Text(string title, string value)
    {
        var prompt = new TextPrompt<string>(title).Validate(input => !string.IsNullOrWhiteSpace(input));
        if (!string.IsNullOrWhiteSpace(value))
        {
            prompt.DefaultValue(value);
        }
        return AnsiConsole.Prompt(prompt);
    }

    private static string? ConnectionText(string title, string? current)
    {
        var input = AnsiConsole.Prompt(new TextPrompt<string>(title + (string.IsNullOrEmpty(current)
            ? " (leave empty to keep unset)" : " (leave empty to keep, enter - to clear)")).AllowEmpty());
        return input.Trim() switch
        {
            "-" => null,
            "" => current,
            var value => value
        };
    }

    private static int Integer(string title, int value, int minimum, int maximum) => AnsiConsole.Prompt(new TextPrompt<int>(title)
        .DefaultValue(value).Validate(input => input >= minimum && input <= maximum));

    private static double Number(string title, double value, double minimum, double maximum) => AnsiConsole.Prompt(new TextPrompt<double>(title)
        .DefaultValue(value).Validate(input => double.IsFinite(input) && input >= minimum && input <= maximum));

}
