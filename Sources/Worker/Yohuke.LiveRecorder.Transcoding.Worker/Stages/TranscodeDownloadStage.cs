using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeDownloadStage : IWorkerStage<TranscodeStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Downloading", 0, 10);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(TranscodeStageContext context, CancellationToken cancellationToken) =>
        WorkerArtifactStore.ValidateAsync(Path.GetDirectoryName(context.Files.SourcePath)!, context.Payload.SourceArtifacts, cancellationToken);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(TranscodeStageContext context, CancellationToken cancellationToken)
    {
        context.Reporter.Start("Downloading", 0, "Downloading the source file.");
        await context.Transfers.DownloadAsync(context.Assignment, context.Files.SourcePath, context.Configuration.DownloadBytesPerSecond,
            _ => { }, cancellationToken, context.Transfer, message => context.Reporter.Message(message),
            invalidateCached: context.Payload.SourceArtifacts.Count > 0);
        context.Payload.SourceArtifacts = await WorkerArtifactStore.CaptureAsync(Path.GetDirectoryName(context.Files.SourcePath)!,
            [Path.GetFileName(context.Files.SourcePath)], cancellationToken);
        return WorkerStageResult.Completed;
    }
}
