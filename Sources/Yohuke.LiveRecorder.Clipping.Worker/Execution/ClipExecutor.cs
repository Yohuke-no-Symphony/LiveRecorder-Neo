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
        if (assignment.Mode is not ("Exact" or "Fast") || !double.IsFinite(assignment.ActualStartMs)
            || !double.IsFinite(assignment.ActualEndMs) || assignment.ActualStartMs < 0
            || assignment.ActualEndMs <= assignment.ActualStartMs || assignment.Inputs.Count == 0)
        {
            throw new InvalidDataException("The clipping assignment has invalid timeline bounds or inputs.");
        }
        var total = assignment.Inputs.Where(input => !input.IsGap).Sum(input =>
            input.Range is { } range ? range.EndOffset - range.StartOffset : throw new InvalidDataException("Missing clipping input range."));
        var completed = 0L;
        var inputs = new List<MediaInputSpan>();
        for (var index = 0; index < assignment.Inputs.Count; index++)
        {
            var input = assignment.Inputs[index];
            if (input.IsGap)
            {
                inputs.Add(new() { SessionStartMs = input.SessionStartMs, DurationMs = input.DurationMs });
                continue;
            }
            var range = input.Range ?? throw new InvalidDataException("Missing clipping input range.");
            var rawPath = Path.Combine(workspace.DirectoryPath, $"input-{index:D5}.range");
            var sourcePath = Path.Combine(workspace.DirectoryPath, $"input-{index:D5}.flv");
            var completedBeforeInput = completed;
            await transfer.DownloadAsync(assignment, input, rawPath, configuration.DownloadBytesPerSecond, cancellationToken,
                bytes => downloadProgress?.Invoke(completedBeforeInput + bytes, total), log);
            await using (var source = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var destination = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await IndexedFlvRange.CopyAsync(source, new()
                {
                    SourceLength = range.SourceLength,
                    StartOffset = range.StartOffset,
                    EndOffset = range.EndOffset,
                    StartMs = range.StartMs,
                    EndMs = range.EndMs,
                    VideoHeader = range.VideoHeader,
                    AudioHeader = range.AudioHeader
                }, destination, cancellationToken);
            }
            File.Delete(rawPath);
            completed += range.EndOffset - range.StartOffset;
            inputs.Add(new()
            {
                Path = sourcePath,
                SessionStartMs = input.SessionStartMs,
                SourceStartMs = Math.Max(0, input.SourceStartMs - range.StartMs),
                DurationMs = input.DurationMs
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        log?.Invoke("Required input ranges downloaded. Exporting the clip.");
        using var processes = new MediaProcessRunner(Options.Create(new MediaToolOptions()), new ClipMediaSettingsStore(configuration));
        var processor = new FfmpegMediaProcessor(processes);
        var result = await processor.ExportAsync(new()
        {
            Inputs = inputs,
            OutputPath = workspace.OutputPath,
            Mode = assignment.Mode,
            KeyframeBoundsPrepared = assignment.Mode == "Fast",
            StartMs = assignment.ActualStartMs,
            DurationMs = assignment.ActualEndMs - assignment.ActualStartMs
        }, new ClipExportProgress(exportProgress), cancellationToken);
        if (Math.Abs(result.ActualStartMs - assignment.ActualStartMs) > 1
            || Math.Abs(result.ActualEndMs - assignment.ActualEndMs) > 1)
        {
            throw new InvalidDataException("The exported clip does not match the server's planned timeline.");
        }
        await using var output = new FileStream(workspace.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return new()
        {
            Bytes = output.Length,
            Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, cancellationToken)),
            ActualStartMs = result.ActualStartMs,
            ActualEndMs = result.ActualEndMs
        };
    }
}
