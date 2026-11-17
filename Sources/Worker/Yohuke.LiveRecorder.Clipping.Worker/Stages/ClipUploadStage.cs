using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipUploadStage : IWorkerStage<ClipStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Uploading", 80, 99);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(ClipStageContext context, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(ClipStageContext context, CancellationToken cancellationToken)
    {
        context.BeginTransfer();
        context.Log("Uploading the validated clip.");
        if (context.SupportsRecovery)
        {
            await context.Transfers.BeginUploadAsync(context.Assignment, context.Payload.Manifest!, cancellationToken);
        }
        await context.Transfers.UploadOutputAsync(context.Assignment, context.Files.OutputPath, context.Payload.Manifest!,
            context.Configuration.UploadBytesPerSecond, cancellationToken,
            completed => context.Transfer(completed, context.Payload.Manifest!.Bytes), context.Log);
        context.Payload.UploadedAttempt = context.Assignment.Attempt;
        return WorkerStageResult.Completed;
    }
}
