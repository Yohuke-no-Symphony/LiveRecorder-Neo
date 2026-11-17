using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Stages;

internal sealed class ClipExportStage : IWorkerStage<ClipStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Encoding", 15, 78);

    /// <inheritdoc />
    public async Task<bool> RestoreAsync(ClipStageContext context, CancellationToken cancellationToken)
    {
        if (context.Payload.OutputPath is null || context.Payload.ExportResult is null)
        {
            return false;
        }
        context.Files.SelectOutput(WorkerArtifactStore.Resolve(context.Files.DirectoryPath, context.Payload.OutputPath));
        return await WorkerArtifactStore.ValidateAsync(context.Files.DirectoryPath, context.Payload.OutputArtifacts, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(ClipStageContext context, CancellationToken cancellationToken)
    {
        context.Log("Exporting the clip.");
        context.Payload.OutputPath = "output-" + Guid.NewGuid().ToString("N") + ".mp4";
        context.Payload.Manifest = null;
        context.Files.SelectOutput(WorkerArtifactStore.Resolve(context.Files.DirectoryPath, context.Payload.OutputPath));
        context.Save();
        context.Payload.ExportResult = await ClipExecutor.ExportAsync(context.Assignment, context.Configuration,
            context.Files, cancellationToken, context.ExportProgress);
        context.Payload.OutputArtifacts = await WorkerArtifactStore.CaptureAsync(context.Files.DirectoryPath,
            [context.Payload.OutputPath], cancellationToken);
        return WorkerStageResult.Completed;
    }
}
