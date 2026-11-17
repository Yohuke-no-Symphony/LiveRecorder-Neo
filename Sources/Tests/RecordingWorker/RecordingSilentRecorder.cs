using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingSilentRecorder : IMediaRecorder
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RecordAsync(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
