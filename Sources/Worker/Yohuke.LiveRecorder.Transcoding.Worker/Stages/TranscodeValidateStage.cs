using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Worker.Common.Stages;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Stages;

internal sealed class TranscodeValidateStage : IWorkerStage<TranscodeStageContext>
{
    private const int MAX_ENCODING_ATTEMPTS = 3;
    public WorkerStageDefinition Definition { get; } = new("Validating", 80, 85);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(TranscodeStageContext context, CancellationToken cancellationToken) => Task.FromResult(context.Payload.Manifest is not null);

    /// <inheritdoc />
    public async Task<WorkerStageResult> ExecuteAsync(TranscodeStageContext context, CancellationToken cancellationToken)
    {
        var payload = context.Payload;
        context.Reporter.Start("Validating", 80, "Validating output media, fragment timelines and checksums.");
        try
        {
            payload.Manifest = await new TranscodeOutputValidator(context.Inspector).ValidateAsync(context.Configuration.FfprobePath,
                context.OutputDirectory, payload.Source!, payload.ExpectedMedia!, payload.Encoder!, cancellationToken, context.Assignment.Profile);
            return WorkerStageResult.Completed;
        }
        catch (TranscodeBitrateException error) when (payload.EncodingAttempts < MAX_ENCODING_ATTEMPTS
            && error.Media.VideoBitrate > 0 && (!payload.Source!.HasAudio || error.Media.AudioBitrate > 0))
        {
            var videoLimit = TranscodeBitratePolicy.GetTargetBitrate(payload.Source!.VideoBitrate, context.Assignment.Profile.VideoBitrateKbps);
            var audioLimit = TranscodeBitratePolicy.GetTargetBitrate(payload.Source.AudioBitrate, context.Assignment.Profile.AudioBitrateKbps);
            var video = LowerTarget(payload.VideoBitrate, error.Media.VideoBitrate, videoLimit);
            var audio = payload.Source.HasAudio ? LowerTarget(payload.AudioBitrate, error.Media.AudioBitrate, audioLimit) : 0;
            if (video == payload.VideoBitrate && audio == payload.AudioBitrate)
            {
                throw;
            }
            payload.VideoBitrate = video;
            payload.AudioBitrate = audio;
            context.Reporter.Message(error.Message + " Re-encoding with corrected bitrate targets.", "Warning");
            Directory.Delete(context.OutputDirectory, recursive: true);
            payload.EncodedArtifacts.Clear();
            return new("Encoding");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            payload.EncodedArtifacts.Clear();
            throw;
        }
    }

    private static long LowerTarget(long current, long actual, long limit) => TranscodeBitratePolicy.IsWithinLimit(actual, limit)
        ? current : Math.Max(1, Math.Min(current - 1, (long)Math.Floor(current * (limit / (double)actual))));
}
