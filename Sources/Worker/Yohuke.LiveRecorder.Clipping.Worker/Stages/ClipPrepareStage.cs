using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipPrepareStage : IWorkerStage<ClipStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Preparing", 12, 15);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(ClipStageContext context, CancellationToken cancellationToken) =>
        context.Assignment.Inputs.All(input => input.IsGap) ? Task.FromResult(true)
            : WorkerArtifactStore.ValidateAsync(context.Files.DirectoryPath, context.Payload.PreparedArtifacts, cancellationToken);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(ClipStageContext context, CancellationToken cancellationToken)
    {
        context.Log("Rebuilding the bounded media inputs.");
        await ClipExecutor.PrepareAsync(context.Assignment, context.Files, cancellationToken);
        context.Payload.PreparedArtifacts = await WorkerArtifactStore.CaptureAsync(context.Files.DirectoryPath,
            context.Assignment.Inputs.Select((input, index) => input.IsGap ? null : $"input-{index:D5}.flv").OfType<string>(), cancellationToken);
        return WorkerStageResult.Completed;
    }
}
