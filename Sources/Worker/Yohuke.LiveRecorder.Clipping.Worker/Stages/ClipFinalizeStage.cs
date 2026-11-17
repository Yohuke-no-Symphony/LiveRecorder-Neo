using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipFinalizeStage : IWorkerStage<ClipStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Finalizing", 99, 100);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(ClipStageContext context, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(ClipStageContext context, CancellationToken cancellationToken)
    {
        context.Log("Waiting for clip verification and publication.");
        await context.Transfers.CompleteAsync(context.Assignment, context.Payload.Manifest!, cancellationToken, context.Log);
        return WorkerStageResult.Completed;
    }
}
