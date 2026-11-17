using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipDownloadStage : IWorkerStage<ClipStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Downloading", 0, 12);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(ClipStageContext context, CancellationToken cancellationToken) =>
        context.Assignment.Inputs.All(input => input.IsGap) ? Task.FromResult(true)
            : WorkerArtifactStore.ValidateAsync(context.Files.DirectoryPath, context.Payload.RawArtifacts, cancellationToken);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(ClipStageContext context, CancellationToken cancellationToken)
    {
        context.BeginTransfer();
        context.Log("Downloading the assigned clipping ranges.");
        await context.Executor.DownloadAsync(context.Assignment, context.Configuration, context.Files, cancellationToken, context.Transfer, context.Log);
        context.Payload.RawArtifacts = await WorkerArtifactStore.CaptureAsync(context.Files.DirectoryPath,
            context.Assignment.Inputs.Select((input, index) => input.IsGap ? null : $"input-{index:D5}.range").OfType<string>(), cancellationToken);
        return WorkerStageResult.Completed;
    }
}
