using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Yohuke.LiveRecorder.Transcoding.Worker.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class TranscodeEncoder(WorkerProcessRunner processes, MediaInspector inspector, Action<EncoderCapability> invalidateEncoder)
{
    private const int MAX_ENCODING_ATTEMPTS = 3;

    internal async Task<TranscodeOutputManifest> EncodeAsync(WorkerConfiguration configuration, WorkerWorkspaceLease files,
        TranscodeMediaInfo source, TranscodeProfile profile, EncodingReservation reservation, WorkerJobReporter reporter,
        CancellationToken cancellationToken)
    {
        var videoLimit = TranscodeBitratePolicy.GetTargetBitrate(source.VideoBitrate, profile.VideoBitrateKbps);
        var audioLimit = TranscodeBitratePolicy.GetTargetBitrate(source.AudioBitrate, profile.AudioBitrateKbps);
        var videoBitrate = videoLimit;
        var audioBitrate = source.HasAudio ? audioLimit : 0;
        var validator = new TranscodeOutputValidator(inspector);
        for (var attempt = 1; ; attempt++)
        {
            var plan = TranscodePlanner.Create(files.SourcePath, files.OutputDirectory, source, profile,
                reservation.Encoder, reservation.Threads, videoBitrate, audioBitrate);
            reporter.Start("Encoding", 15, FormattableString.Invariant(
                $"Starting encode with {reservation.Encoder.Encoder}, thread budget {reservation.Threads}, attempt {attempt}/{MAX_ENCODING_ATTEMPTS}, video target {videoBitrate} bps, audio target {audioBitrate} bps."), reservation.Encoder.Encoder);
            var result = await processes.RunAsync(configuration.FfmpegPath, plan.Arguments, cancellationToken,
                errorLine: message => reporter.Message("FFmpeg: " + message, "Error"),
                encodingProgress: value => reporter.Encoding(value, source.DurationMs));
            if (result.ExitCode != 0)
            {
                invalidateEncoder(reservation.Encoder);
                throw new InvalidDataException("FFmpeg encoding failed: " + result.Diagnostic);
            }
            reporter.Start("Validating", 80, "Encoding completed. Validating output media, fragment timelines and checksums.");
            try
            {
                return await validator.ValidateAsync(configuration.FfprobePath, files.OutputDirectory, source,
                    plan.ExpectedMedia, reservation.Encoder.Encoder, cancellationToken, profile);
            }
            catch (TranscodeBitrateException exception) when (attempt < MAX_ENCODING_ATTEMPTS
                && exception.Media.VideoBitrate > 0 && (!source.HasAudio || exception.Media.AudioBitrate > 0))
            {
                var nextVideoBitrate = LowerTarget(videoBitrate, exception.Media.VideoBitrate, videoLimit);
                var nextAudioBitrate = source.HasAudio ? LowerTarget(audioBitrate, exception.Media.AudioBitrate, audioLimit) : 0;
                if (nextVideoBitrate == videoBitrate && nextAudioBitrate == audioBitrate)
                {
                    throw;
                }
                reporter.Message(exception.Message + FormattableString.Invariant(
                    $" Re-encoding with video target {nextVideoBitrate} bps and audio target {nextAudioBitrate} bps."), "Warning");
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(files.OutputDirectory, recursive: true);
                Directory.CreateDirectory(files.OutputDirectory);
                videoBitrate = nextVideoBitrate;
                audioBitrate = nextAudioBitrate;
            }
        }
    }

    private static long LowerTarget(long current, long actual, long limit)
    {
        if (TranscodeBitratePolicy.IsWithinLimit(actual, limit))
        {
            return current;
        }
        return Math.Max(1, Math.Min(current - 1, (long)Math.Floor(current * (limit / (double)actual))));
    }
}
