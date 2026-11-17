using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeFinalizeStage : IWorkerStage<TranscodeStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Finalizing", 99, 100);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(TranscodeStageContext context, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(TranscodeStageContext context, CancellationToken cancellationToken)
    {
        context.Reporter.Start("Finalizing", 99, "Waiting for server verification and publication.");
        await context.Transfers.CompleteAsync(context.Assignment, context.Payload.Manifest!, cancellationToken,
            message => context.Reporter.Message(message));
        return WorkerStageResult.Completed;
    }
}
