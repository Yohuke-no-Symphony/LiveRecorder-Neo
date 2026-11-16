using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingRetryTestRecorder : IMediaRecorder
{
    private int attempts;

    internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource EndFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource SecondStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RecordAsync(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref attempts) == 1)
        {
            progress.Report(new()
            {
                SegmentId = "first", CommittedBytes = 1000, DurationMs = 1000,
                CommitSnapshot = new() { SegmentId = "first", DurationMs = 1000 }
            });
            FirstStarted.TrySetResult();
            await EndFirst.Task.WaitAsync(cancellationToken);
            return;
        }
        progress.Report(new() { Kind = "Opened", SegmentId = "second", CommittedBytes = 500 });
        SecondStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            SecondStopped.TrySetResult();
        }
    }
}
