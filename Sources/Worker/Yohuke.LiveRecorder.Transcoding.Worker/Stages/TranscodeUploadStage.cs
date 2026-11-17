using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeUploadStage : IWorkerStage<TranscodeStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Uploading", 85, 99);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(TranscodeStageContext context, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(TranscodeStageContext context, CancellationToken cancellationToken)
    {
        context.Reporter.Start("Uploading", 85, "Uploading the validated output.");
        if (context.SupportsRecovery)
        {
            await context.Transfers.BeginUploadAsync(context.Assignment, context.Payload.Manifest!, cancellationToken);
        }
        await context.Transfers.UploadArtifactsAsync(context.Assignment, context.OutputDirectory, context.Payload.Manifest!,
            context.Configuration.UploadBytesPerSecond, _ => { }, cancellationToken, context.Transfer,
            message => context.Reporter.Message(message));
        context.Payload.UploadedAttempt = context.Assignment.Attempt;
        return WorkerStageResult.Completed;
    }
}
