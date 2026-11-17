using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker.Storage;
using Yohuke.LiveRecorder.Clipping.Worker.Transport;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Clipping.Worker.Execution;

internal sealed class ClipExecutor(ClipFileTransfer transfer)
{
    internal async Task<ClipOutputManifest> ExecuteAsync(ClipAssignment assignment, WorkerConfiguration configuration,
        ClipWorkspaceLease workspace, CancellationToken cancellationToken, Action<long, long>? downloadProgress = null,
        Action<double>? exportProgress = null, Action<string>? log = null)
    {
        await DownloadAsync(assignment, configuration, workspace, cancellationToken, downloadProgress, log);
        await PrepareAsync(assignment, workspace, cancellationToken);
        var result = await ExportAsync(assignment, configuration, workspace, cancellationToken, exportProgress);
        return await ValidateAsync(assignment, workspace.OutputPath, result, cancellationToken);
    }

    internal async Task DownloadAsync(ClipAssignment assignment, WorkerConfiguration configuration,
        ClipWorkspaceLease workspace, CancellationToken cancellationToken, Action<long, long>? progress, Action<string>? log)
    {
        ValidateAssignment(assignment);
        var total = assignment.Inputs.Where(input => !input.IsGap).Sum(input => input.Range!.EndOffset - input.Range.StartOffset);
        var completed = 0L;
        for (var index = 0; index < assignment.Inputs.Count; index++)
        {
            var input = assignment.Inputs[index];
            if (input.IsGap)
            {
                continue;
            }
            var before = completed;
            await transfer.DownloadAsync(assignment, input, Path.Combine(workspace.DirectoryPath, $"input-{index:D5}.range"),
                configuration.DownloadBytesPerSecond, cancellationToken, bytes => progress?.Invoke(before + bytes, total), log);
            completed += input.Range!.EndOffset - input.Range.StartOffset;
        }
    }

    internal static async Task PrepareAsync(ClipAssignment assignment, ClipWorkspaceLease workspace, CancellationToken cancellationToken)
    {
        ValidateAssignment(assignment);
        for (var index = 0; index < assignment.Inputs.Count; index++)
        {
            var input = assignment.Inputs[index];
            if (input.IsGap)
            {
                continue;
            }
            var range = input.Range!;
            var sourcePath = Path.Combine(workspace.DirectoryPath, $"input-{index:D5}.flv");
            var temporary = sourcePath + ".preparing";
            await using (var source = new FileStream(Path.Combine(workspace.DirectoryPath, $"input-{index:D5}.range"),
                FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await IndexedFlvRange.CopyAsync(source, new()
                {
                    SourceLength = range.SourceLength, StartOffset = range.StartOffset, EndOffset = range.EndOffset,
                    StartMs = range.StartMs, EndMs = range.EndMs, VideoHeader = range.VideoHeader, AudioHeader = range.AudioHeader
                }, destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            File.Move(temporary, sourcePath, overwrite: true);
        }
    }

    internal static async Task<MediaExportResult> ExportAsync(ClipAssignment assignment, WorkerConfiguration configuration,
        ClipWorkspaceLease workspace, CancellationToken cancellationToken, Action<double>? progress)
    {
        var inputs = assignment.Inputs.Select((input, index) => input.IsGap
            ? new MediaInputSpan { SessionStartMs = input.SessionStartMs, DurationMs = input.DurationMs }
            : new MediaInputSpan
            {
                Path = Path.Combine(workspace.DirectoryPath, $"input-{index:D5}.flv"), SessionStartMs = input.SessionStartMs,
                SourceStartMs = Math.Max(0, input.SourceStartMs - input.Range!.StartMs), DurationMs = input.DurationMs
            }).ToList();
        using var processes = new MediaProcessRunner(Options.Create(new MediaToolOptions()), new ClipMediaSettingsStore(configuration));
        return await new FfmpegMediaProcessor(processes).ExportAsync(new()
        {
            Inputs = inputs, OutputPath = workspace.OutputPath, Mode = assignment.Mode,
            EncodingThreads = configuration.ClipEncodingThreads, KeyframeBoundsPrepared = assignment.Mode == "Fast",
            StartMs = assignment.ActualStartMs, DurationMs = assignment.ActualEndMs - assignment.ActualStartMs
        }, new ClipExportProgress(progress), cancellationToken);
    }

    internal static async Task<ClipOutputManifest> ValidateAsync(ClipAssignment assignment, string outputPath,
        MediaExportResult result, CancellationToken cancellationToken)
    {
        if (Math.Abs(result.ActualStartMs - assignment.ActualStartMs) > 1 || Math.Abs(result.ActualEndMs - assignment.ActualEndMs) > 1)
        {
            throw new InvalidDataException("The exported clip does not match the server's planned timeline.");
        }
        await using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return new()
        {
            Bytes = output.Length, Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, cancellationToken)),
            ActualStartMs = result.ActualStartMs, ActualEndMs = result.ActualEndMs
        };
    }

    private static void ValidateAssignment(ClipAssignment assignment)
    {
        if (assignment.Mode is not ("Exact" or "Fast") || !double.IsFinite(assignment.ActualStartMs)
            || !double.IsFinite(assignment.ActualEndMs) || assignment.ActualStartMs < 0
            || assignment.ActualEndMs <= assignment.ActualStartMs || assignment.Inputs.Count == 0
            || assignment.Inputs.Any(input => !input.IsGap && input.Range is null))
        {
            throw new InvalidDataException("The clipping assignment has invalid timeline bounds or inputs.");
        }
    }
}
