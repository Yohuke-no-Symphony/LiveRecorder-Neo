using System.Diagnostics;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

internal sealed class MediaFixture : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-media-tests", Guid.NewGuid().ToString("N"));
    internal TestSettingsStore Settings { get; } = new();
    internal MediaProcessRunner Processes { get; }
    internal FfmpegMediaProcessor Processor { get; }

    internal MediaFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        Settings.Value.MinimumFreeBytes = 0;
        Processes = new(Options.Create(new MediaToolOptions()), Settings);
        Processor = new(Processes);
    }

    internal async Task<string> CreateSampleAsync(string name = "sample.flv", string size = "160x90", double duration = 4, int keyframeInterval = 10)
    {
        var path = Path.Combine(DirectoryPath, name);
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc=size={size}:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", duration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-g", keyframeInterval.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", path
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error);
        }
        return path;
    }

    internal async Task<List<MediaRecordingUpdate>> RecordAsync(string sourcePath, long segmentBytes = 0, string? title = null)
    {
        using var client = new HttpClient(new RecordingHttpHandler(await File.ReadAllBytesAsync(sourcePath)));
        var recorder = new FlvMediaRecorder(client, Options.Create(new MediaToolOptions()), Settings);
        var updates = new List<MediaRecordingUpdate>();
        var started = DateTime.UtcNow;
        await recorder.RecordAsync(new()
        {
            StreamUrl = "https://example.invalid/live.flv",
            OutputDirectory = DirectoryPath,
            Title = title ?? "直播/测试:sample",
            StartedAtUtc = started,
            SessionStartedAtUtc = started,
            MaxSegmentBytes = segmentBytes
        }, new InlineProgress<MediaRecordingUpdate>(updates.Add), CancellationToken.None);
        return updates;
    }

    public void Dispose()
    {
        Processes.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
