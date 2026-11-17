using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeInspectStage : IWorkerStage<TranscodeStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Inspecting", 10, 15);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(TranscodeStageContext context, CancellationToken cancellationToken) =>
        Task.FromResult(context.Payload.Source is { DurationMs: > 0, Width: > 0, Height: > 0 });

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(TranscodeStageContext context, CancellationToken cancellationToken)
    {
        context.Reporter.Start("Inspecting", 10, "Inspecting source media.");
        context.Payload.Source = await context.Inspector.InspectInputAsync(context.Configuration.FfprobePath, context.Files.SourcePath,
            cancellationToken, message => context.Reporter.Message(message));
        _ = TranscodePlanner.Create(context.Files.SourcePath, context.Files.OutputDirectory, context.Payload.Source,
            context.Assignment.Profile, new EncoderCapability { Encoder = "libx264", Available = true }, 1);
        return WorkerStageResult.Completed;
    }
}
