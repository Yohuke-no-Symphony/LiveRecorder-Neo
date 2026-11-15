using Spectre.Console;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal static class WorkerSetup
{
    internal static WorkerConnectionOptions ConfigureConnection(WorkerConnectionOptions current)
    {
        RequireInteractive();
        AnsiConsole.MarkupLine("[bold]转码 Worker 绑定[/]（保存到 settings.json）");
        var result = current with
        {
            ApiBaseUrl = Text("主站 HTTPS 地址", current.ApiBaseUrl).Trim(),
            ControlHost = Text("TCP 控制主机", current.ControlHost).Trim(),
            ControlPort = Integer("TCP 控制端口", current.ControlPort, 1, 65535),
            WorkerId = Text("主站生成的 Worker ID", current.WorkerId).Trim()
        };
        var preserveSecret = result.WorkerId == current.WorkerId && result.ControlHost == current.ControlHost
            && result.ControlPort == current.ControlPort && result.ApiBaseUrl == current.ApiBaseUrl
            && !string.IsNullOrWhiteSpace(current.Secret);
        var secretPrompt = new TextPrompt<string>(preserveSecret ? "Worker Secret（留空保留现有密钥）" : "主站生成的 Worker Secret")
            .Secret().HideDefaultValue().Validate(value => value.Length <= 256 && (preserveSecret || !string.IsNullOrWhiteSpace(value)));
        if (preserveSecret)
        {
            secretPrompt.AllowEmpty();
        }
        var secret = AnsiConsole.Prompt(secretPrompt);
        result.Secret = preserveSecret && string.IsNullOrWhiteSpace(secret) ? current.Secret : secret;
        if (AnsiConsole.Confirm("配置 TLS 主机名或私有证书指纹？", false))
        {
            result.TlsServerName = ConnectionText("TLS 控制证书主机名", current.TlsServerName);
            result.ServerCertificateSha256 = ConnectionText("通用证书 SHA-256 指纹", current.ServerCertificateSha256);
            result.ControlCertificateSha256 = ConnectionText("TCP 控制证书 SHA-256 指纹", current.ControlCertificateSha256);
            result.ApiCertificateSha256 = ConnectionText("HTTPS 证书 SHA-256 指纹", current.ApiCertificateSha256);
        }
        result.Validate();
        return result;
    }

    internal static WorkerConfiguration Configure(WorkerConfiguration current)
    {
        RequireInteractive();
        AnsiConsole.MarkupLine("[bold]转码 Worker 运行配置[/]（保存到 settings.json 并同步主站）");
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
            DeviceId = ConnectionText("设备标识，未设置时自动选择", current.DeviceId),
            MaxCacheBytes = (long)(Number("临时缓存上限（GiB）", current.MaxCacheBytes / (1024d * 1024 * 1024), 1d / 1024, 1048576) * 1024 * 1024 * 1024),
            MinimumFreeBytes = (long)(Number("保留磁盘空间（GiB）", current.MinimumFreeBytes / (1024d * 1024 * 1024), 0, 1048576) * 1024 * 1024 * 1024)
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

    private static void RequireInteractive()
    {
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new InvalidOperationException("当前终端不能交互，请在交互终端运行 --bind 或 --configure，或预先填写 --config 指定的 settings.json");
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
            ? "（留空不设置）" : "（留空保留，输入 - 清除）")).AllowEmpty());
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
