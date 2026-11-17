using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Recording.Worker.Capture;

internal sealed class RecordingProgress(Action<MediaRecordingUpdate> report) : IProgress<MediaRecordingUpdate>
{
    /// <inheritdoc />
    public void Report(MediaRecordingUpdate value) => report(value);
}
