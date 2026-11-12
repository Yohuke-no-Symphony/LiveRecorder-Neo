using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class JobTestMediaProcessor : IMediaProcessor
{
    internal int ExportCalls { get; private set; }
    internal int ProbeCalls { get; private set; }
    internal int RepairCalls { get; private set; }
    internal int RepairOutputCount { get; set; } = 2;
    internal bool InvalidSecondRepairOutput { get; set; }
    internal bool BlockExport { get; set; }
    internal TaskCompletionSource ExportEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ContinueExport { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        ProbeCalls++;
        return Task.FromResult(new MediaProbe
        {
            DurationMs = InvalidSecondRepairOutput && path.EndsWith("_p1.flv", StringComparison.Ordinal) ? 0 : Path.GetFileName(path).Contains("_p", StringComparison.Ordinal) ? 500 : 1000,
            VideoCodec = "h264",
            AudioCodec = "aac",
            Width = 160,
            Height = 90,
            FrameRate = 10,
            HasAudio = true
        });
    }

    public async Task<MediaRepairResult> RepairAsync(string inputPath, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        RepairCalls++;
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Repair source was resolved incorrectly.", inputPath);
        }
        var result = new MediaRepairResult();
        for (var index = 0; index < RepairOutputCount; index++)
        {
            var path = Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + "_p" + index + ".flv");
            var indexPath = Path.ChangeExtension(path, ".index.jsonl");
            await File.WriteAllTextAsync(path, "verified-media-fixture-" + index, cancellationToken);
            await File.WriteAllTextAsync(indexPath, "{\"kind\":\"Keyframe\",\"offset\":13}\n", cancellationToken);
            result.Outputs.Add(new() { Path = path, IndexPath = indexPath, SourceStartMs = index * 500, DurationMs = 500 });
        }
        progress.Report(1);
        return result;
    }

    public async Task<MediaExportResult> ExportAsync(MediaExportRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ExportCalls++;
        if (request.Inputs.Any(input => !input.IsGap && !File.Exists(input.Path)))
        {
            throw new FileNotFoundException("Export source was resolved incorrectly.");
        }
        ExportEntered.TrySetResult();
        if (BlockExport)
        {
            await ContinueExport.Task.WaitAsync(cancellationToken);
        }
        await File.WriteAllTextAsync(request.OutputPath, "verified-export-fixture", cancellationToken);
        progress.Report(1);
        return new() { ActualStartMs = request.StartMs, ActualEndMs = request.StartMs + request.DurationMs, Bytes = new FileInfo(request.OutputPath).Length };
    }
}
