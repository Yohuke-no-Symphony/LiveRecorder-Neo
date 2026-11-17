using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Yohuke.LiveRecorder.Transcoding.Worker.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Encoding;

internal sealed class TranscodeEncoder(WorkerProcessRunner processes, Action<EncoderCapability> invalidateEncoder)
{
    internal async Task<TranscodeMediaInfo> EncodeOnceAsync(WorkerConfiguration configuration, WorkerWorkspaceLease files,
        TranscodeMediaInfo source, TranscodeProfile profile, EncodingReservation reservation, long videoBitrate,
        long audioBitrate, WorkerJobReporter reporter, Action<WorkerEncodingProgress> progress, CancellationToken cancellationToken)
    {
        var plan = TranscodePlanner.Create(files.SourcePath, files.OutputDirectory, source, profile,
            reservation.Encoder, reservation.Threads, videoBitrate, audioBitrate);
        reporter.Start("Encoding", 15, FormattableString.Invariant(
            $"Starting encode with {reservation.Encoder.Encoder}, thread budget {reservation.Threads}, video target {videoBitrate} bps, audio target {audioBitrate} bps."), reservation.Encoder.Encoder);
        var result = await processes.RunAsync(configuration.FfmpegPath, plan.Arguments, cancellationToken,
            errorLine: message => reporter.Message("FFmpeg: " + message, "Error"), encodingProgress: progress);
        if (result.ExitCode != 0)
        {
            invalidateEncoder(reservation.Encoder);
            throw new InvalidDataException("FFmpeg encoding failed: " + result.Diagnostic);
        }
        return plan.ExpectedMedia;
    }
}
