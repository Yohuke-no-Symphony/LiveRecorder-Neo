using System.Runtime.InteropServices;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class EncoderProbe(WorkerProcessRunner processes)
{
    internal async Task<WorkerCapabilities> ProbeAsync(WorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        using var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        toolTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var version = await processes.RunAsync(configuration.FfmpegPath, ["-version"], toolTimeout.Token);
        if (version.ExitCode != 0)
        {
            throw new InvalidOperationException("FFmpeg 版本检查失败：" + version.Diagnostic);
        }
        var capabilities = new WorkerCapabilities
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ToolVersion = version.Output.Split('\n')[0].Trim()
        };
        foreach (var candidate in Candidates(configuration))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
                arguments.AddRange(EncoderArguments.Device(candidate));
                arguments.AddRange(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-t", "0.5", "-an", "-filter_threads", "1"]);
                if (candidate.Encoder == "h264_vaapi")
                {
                    arguments.AddRange(["-vf", "format=nv12,hwupload"]);
                }
                else
                {
                    arguments.AddRange(["-pix_fmt", "yuv420p"]);
                }
                arguments.AddRange(EncoderArguments.Encoding(candidate, 1));
                arguments.AddRange(["-b:v", "2500k", "-g", "60", "-f", "null", "-"]);
                var result = await processes.RunAsync(configuration.FfmpegPath, arguments, timeout.Token);
                candidate.Available = result.ExitCode == 0;
                candidate.Error = candidate.Available ? null : Compact(result.Diagnostic);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                              && exception is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                candidate.Error = Compact(exception.Message);
            }
            capabilities.Encoders.Add(candidate);
        }
        return capabilities;
    }

    private static IEnumerable<EncoderCapability> Candidates(WorkerConfiguration configuration)
    {
        yield return new() { Encoder = "libx264" };
        if (OperatingSystem.IsMacOS())
        {
            yield return new() { Encoder = "h264_videotoolbox" };
            yield break;
        }
        yield return new() { Encoder = "h264_nvenc", DeviceId = configuration.DeviceId };
        if (OperatingSystem.IsWindows())
        {
            yield return new() { Encoder = "h264_qsv", DeviceId = configuration.DeviceId };
            yield return new() { Encoder = "h264_amf", DeviceId = configuration.DeviceId };
        }
        else if (OperatingSystem.IsLinux())
        {
            var devices = configuration.DeviceId is { Length: > 0 } device
                ? new[] { device }
                : Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri", "renderD*").Order(StringComparer.Ordinal).ToArray() : [];
            foreach (var path in devices)
            {
                yield return new() { Encoder = "h264_qsv", DeviceId = path };
                yield return new() { Encoder = "h264_vaapi", DeviceId = path };
            }
            yield return new() { Encoder = "h264_amf" };
        }
    }

    private static string Compact(string value) => value.Length <= 2000 ? value : value[..2000];
}
