using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingTestRecorder(Action<IProgress<MediaRecordingUpdate>>? onCancellation = null) : IMediaRecorder
{
    internal TaskCompletionSource<RecordingRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RecordAsync(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, CancellationToken cancellationToken)
    {
        var stopped = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await using var registration = onCancellation is null ? default : cancellationToken.Register(() => onCancellation(progress));
        Started.TrySetResult(request);
        progress.Report(new()
        {
            SegmentId = "segment", CommittedBytes = 1000, DurationMs = 1000,
            CommitSnapshot = new() { SegmentId = "segment", DurationMs = 1000,
                StartOffsetMs = (request.StartedAtUtc - request.SessionStartedAtUtc).TotalMilliseconds }
        });
        try
        {
            await stopped;
        }
        finally
        {
            Stopped.TrySetResult();
        }
    }
}
