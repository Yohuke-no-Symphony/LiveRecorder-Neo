using Spectre.Console;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerSetup
{
    internal static WorkerConfiguration Configure(WorkerConfiguration current)
    {
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new InvalidOperationException("当前终端不能交互，请在主站管理后台配置 Worker，或在交互终端运行 --configure");
        }
        AnsiConsole.MarkupLine("[bold]转码 Worker 配置[/]（保存到主站）");
        var result = current with
        {
            FfmpegPath = Text("FFmpeg 路径", current.FfmpegPath),
            FfprobePath = Text("ffprobe 路径", current.FfprobePath),
            TempDirectory = Text("本机临时目录", string.IsNullOrWhiteSpace(current.TempDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yohuke.LiveRecorder", "Worker")
                : current.TempDirectory),
            MaxConcurrentEncodes = Integer("总同时编码数（GPU＋CPU）", current.MaxConcurrentEncodes, 1, 64),
            GpuConcurrency = Integer("GPU 同时编码数", current.GpuConcurrency, 0, 64),
            CpuConcurrency = Integer("CPU 补位同时编码数", current.CpuConcurrency, 0, 64),
            CpuFallbackMode = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("CPU 补位策略")
                .AddChoices(new[] { current.CpuFallbackMode, "Balanced", "Disabled" }.Distinct())),
            PreferredEncoder = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("优先编码器")
                .AddChoices(new[] { current.PreferredEncoder, "auto" }.Concat(EncoderArguments.ENCODERS.Where(value => value != "libx264")).Distinct())),
            DeviceId = OptionalText("设备标识（留空自动选择）", current.DeviceId),
            MaxCacheBytes = Integer("临时缓存上限（GiB）", (int)(current.MaxCacheBytes / (1024L * 1024 * 1024)), 1, 1048576) * 1024L * 1024 * 1024,
            MinimumFreeBytes = Integer("保留磁盘空间（GiB）", (int)(current.MinimumFreeBytes / (1024L * 1024 * 1024)), 1, 1048576) * 1024L * 1024 * 1024
        };
        if (AnsiConsole.Confirm("配置 CPU 补位阈值和传输限速？", false))
        {
            result.GpuWaitSeconds = Integer("补位前等待 GPU（秒）", current.GpuWaitSeconds, 0, 86400);
            result.CpuSampleWindowSeconds = Integer("CPU 采样窗口（秒）", current.CpuSampleWindowSeconds, 5, 300);
            result.CpuLoadThreshold = Number("启动 CPU 补位的负载阈值（%）", current.CpuLoadThreshold, 1, 100);
            result.CpuThreadFraction = Number("CPU 编码线程预算比例（默认 0.5）", current.CpuThreadFraction, 0.01, 1);
            result.DownloadBytesPerSecond = (long)(Number("下载上限（MiB/s，0 不限速）", current.DownloadBytesPerSecond / (1024d * 1024), 0, 1048576) * 1024 * 1024);
            result.UploadBytesPerSecond = (long)(Number("上传上限（MiB/s，0 不限速）", current.UploadBytesPerSecond / (1024d * 1024), 0, 1048576) * 1024 * 1024);
        }
        result.TempDirectory = Path.GetFullPath(result.TempDirectory);
        result.DeviceId = string.IsNullOrWhiteSpace(result.DeviceId) ? null : result.DeviceId;
        return result;
    }

    internal static void Validate(WorkerConfiguration configuration)
    {
        TranscodeConfigurationPolicy.Validate(configuration);
        if (string.IsNullOrWhiteSpace(configuration.FfmpegPath) || string.IsNullOrWhiteSpace(configuration.FfprobePath)
            || string.IsNullOrWhiteSpace(configuration.TempDirectory) || !Path.IsPathFullyQualified(configuration.TempDirectory))
        {
            throw new InvalidDataException("请配置 FFmpeg、ffprobe 以及绝对路径的临时目录");
        }
    }

    private static string Text(string title, string value) => AnsiConsole.Prompt(new TextPrompt<string>(title)
        .DefaultValue(value).Validate(input => !string.IsNullOrWhiteSpace(input)));

    private static int Integer(string title, int value, int minimum, int maximum) => AnsiConsole.Prompt(new TextPrompt<int>(title)
        .DefaultValue(value).Validate(input => input >= minimum && input <= maximum));

    private static double Number(string title, double value, double minimum, double maximum) => AnsiConsole.Prompt(new TextPrompt<double>(title)
        .DefaultValue(value).Validate(input => double.IsFinite(input) && input >= minimum && input <= maximum));

    private static string OptionalText(string title, string? value)
    {
        var prompt = new TextPrompt<string>(title).AllowEmpty();
        if (!string.IsNullOrWhiteSpace(value))
        {
            prompt.DefaultValue(value);
        }
        return AnsiConsole.Prompt(prompt);
    }
}
