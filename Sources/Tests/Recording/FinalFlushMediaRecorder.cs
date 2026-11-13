using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class FinalFlushMediaRecorder(IMediaRecorder inner) : IMediaRecorder
{
    public async Task RecordAsync(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, CancellationToken cancellationToken)
    {
        var buffered = new RecordingProgressCollector();
        try
        {
            await inner.RecordAsync(request, buffered, cancellationToken);
        }
        finally
        {
            foreach (var update in buffered.Values)
            {
                progress.Report(update);
            }
        }
    }
}
