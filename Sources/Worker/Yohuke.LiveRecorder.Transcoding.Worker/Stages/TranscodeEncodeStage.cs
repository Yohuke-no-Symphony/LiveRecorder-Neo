using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeEncodeStage : IWorkerStage<TranscodeStageContext>
{
    public WorkerStageDefinition Definition { get; } = new("Encoding", 15, 80);

    /// <inheritdoc />
    public async Task<bool> RestoreAsync(TranscodeStageContext context, CancellationToken cancellationToken)
    {
        if (context.Payload.OutputDirectory is null || context.Payload.ExpectedMedia is null)
        {
            return false;
        }
        context.Files.SelectOutput(context.OutputDirectory);
        return await WorkerArtifactStore.ValidateAsync(context.OutputDirectory, context.Payload.EncodedArtifacts, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(TranscodeStageContext context, CancellationToken cancellationToken)
    {
        context.Report(0, substate: "WaitingForResources");
        var selected = await context.Reserve(cancellationToken);
        using var reservation = selected.Reservation;
        context.Configuration = selected.Configuration.Configuration;
        var payload = context.Payload;
        var source = payload.Source!;
        payload.OutputDirectory = "output-" + Guid.NewGuid().ToString("N");
        context.Files.SelectOutput(context.OutputDirectory);
        Directory.CreateDirectory(context.OutputDirectory);
        payload.VideoBitrate = payload.VideoBitrate > 0 ? payload.VideoBitrate
            : TranscodeBitratePolicy.GetTargetBitrate(source.VideoBitrate, context.Assignment.Profile.VideoBitrateKbps);
        payload.AudioBitrate = source.HasAudio ? payload.AudioBitrate > 0 ? payload.AudioBitrate
            : TranscodeBitratePolicy.GetTargetBitrate(source.AudioBitrate, context.Assignment.Profile.AudioBitrateKbps) : 0;
        payload.EncodingAttempts++;
        payload.Encoder = reservation.Encoder.Encoder;
        payload.Manifest = null;
        context.Save();
        payload.ExpectedMedia = await context.Encoder.EncodeOnceAsync(context.Configuration, context.Files, source,
            context.Assignment.Profile, reservation, payload.VideoBitrate, payload.AudioBitrate, context.Reporter,
            context.EncodingProgress, cancellationToken);
        payload.EncodedArtifacts = await WorkerArtifactStore.CaptureAsync(context.OutputDirectory,
            Directory.EnumerateFiles(context.OutputDirectory).Select(Path.GetFileName).OfType<string>(), cancellationToken);
        return WorkerStageResult.Completed;
    }
}
