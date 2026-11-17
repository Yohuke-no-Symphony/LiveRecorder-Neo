using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipValidateStage : IWorkerStage<ClipStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Validating", 78, 80);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(ClipStageContext context, CancellationToken cancellationToken) => Task.FromResult(context.Payload.Manifest is not null);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(ClipStageContext context, CancellationToken cancellationToken)
    {
        context.Log("Validating clip bounds and checksum.");
        try
        {
            context.Payload.Manifest = await ClipExecutor.ValidateAsync(context.Assignment, context.Files.OutputPath,
                context.Payload.ExportResult!, cancellationToken);
            return WorkerStageResult.Completed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Payload.OutputArtifacts.Clear();
            throw;
        }
    }
}
