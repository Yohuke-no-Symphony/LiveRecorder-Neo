using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Clipping.Worker.Tests;

internal sealed class ClipTestFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-clipping-tests", Guid.NewGuid().ToString("N"));
    internal WorkerConfiguration Configuration { get; }
    internal WorkerConnectionOptions Connection { get; } = new()
    {
        ApiBaseUrl = "https://example.invalid/", WorkerId = "clip-worker", Secret = "clip-secret", ControlUseTls = false
    };
    internal MediaProcessRunner Processes { get; }
    internal FfmpegMediaProcessor Processor { get; }

    internal ClipTestFixture()
    {
        Directory.CreateDirectory(Root);
        Configuration = new() { TempDirectory = Root, MaxCacheBytes = 512L * 1024 * 1024, MinimumFreeBytes = 0 };
        Processes = new(Options.Create(new MediaToolOptions()), new ClipMediaSettingsStore(Configuration));
        Processor = new(Processes);
    }

    internal async Task<(ClipAssignment Assignment, byte[] Source)> CreateAssignmentAsync(string mode, bool gap = false)
    {
        var sample = Path.Combine(Root, "sample.flv");
        var process = await new WorkerProcessRunner().RunAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "8",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-y", sample
        ], CancellationToken.None);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.Diagnostic);
        }
        var repaired = await Processor.RepairAsync(sample, Path.Combine(Root, "indexed.flv"), new ClipExportProgress(null), CancellationToken.None);
        var source = repaired.Outputs[0];
        var bytes = await File.ReadAllBytesAsync(source.Path);
        var plan = await IndexedFlvRange.PlanAsync(source.IndexPath, bytes.Length, 2750, 900, CancellationToken.None);
        var start = mode == "Fast" ? plan.StartMs : 2750;
        var end = mode == "Fast" ? plan.EndMs : 3650;
        var span = new ClipInputSpan
        {
            Id = "input-1", SourceETag = "\"fixed-source\"", SourceVersion = "source-v1", SourceFileName = "source.flv",
            RangeSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(checked((int)plan.StartOffset), checked((int)plan.Length)))),
            SourceStartMs = start, SessionStartMs = start + 10000, DurationMs = end - start,
            Range = new()
            {
                SourceLength = bytes.Length, StartOffset = plan.StartOffset, EndOffset = plan.EndOffset,
                StartMs = plan.StartMs, EndMs = plan.EndMs, VideoHeader = plan.VideoHeader, AudioHeader = plan.AudioHeader
            }
        };
        var inputs = new List<ClipInputSpan> { span };
        if (gap)
        {
            inputs.Add(new() { Id = "gap", IsGap = true, SessionStartMs = end + 10000, DurationMs = 500 });
            inputs.Add(span with { Id = "input-2", SessionStartMs = end + 10500 });
        }
        return (new()
        {
            JobId = "clip-job", Attempt = 1, LeaseToken = "lease", Mode = mode,
            StartMs = start + 10000, EndMs = gap ? end + 10500 + span.DurationMs : end + 10000,
            ActualStartMs = start + 10000, ActualEndMs = gap ? end + 10500 + span.DurationMs : end + 10000,
            Inputs = inputs
        }, bytes);
    }

    internal static ClipAssignment SmallAssignment(string id = "job", long sourceLength = 1000) => new()
    {
        JobId = id, Attempt = 1, LeaseToken = "lease", StartMs = 0, EndMs = 1000, ActualStartMs = 0, ActualEndMs = 1000,
        Inputs = [new()
        {
            Id = "source", DurationMs = 1000, SourceETag = "\"source\"",
            RangeSha256 = Convert.ToHexStringLower(SHA256.HashData(new byte[100])),
            Range = new() { SourceLength = sourceLength, StartOffset = 100, EndOffset = 200, StartMs = 0, EndMs = 1000, VideoHeader = [0x17] }
        }]
    };

    /// <inheritdoc />
    public void Dispose()
    {
        Processes.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
