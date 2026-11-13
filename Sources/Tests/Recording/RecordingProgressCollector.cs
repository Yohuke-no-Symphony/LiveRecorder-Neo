using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class RecordingProgressCollector : IProgress<MediaRecordingUpdate>
{
    private readonly List<MediaRecordingUpdate> values = [];

    internal IReadOnlyList<MediaRecordingUpdate> Values => values;

    public void Report(MediaRecordingUpdate value)
    {
        values.Add(value);
    }
}
