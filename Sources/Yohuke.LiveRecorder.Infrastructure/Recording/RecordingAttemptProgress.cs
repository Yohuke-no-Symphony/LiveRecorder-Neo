using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>识别有效媒体进度，在场次发布前合并回调并保留首次媒体。</summary>
public sealed class RecordingAttemptProgress(RecordingInactivityMonitor inactivity) : IProgress<MediaRecordingUpdate>
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, MediaRecordingUpdate> latest = new();
    private readonly TaskCompletionSource mediaReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProgress<MediaRecordingUpdate>? sink;

    public Task MediaReceived => mediaReceived.Task;
    public bool HasMedia => mediaReceived.Task.IsCompletedSuccessfully;

    /// <inheritdoc />
    public void Report(MediaRecordingUpdate value)
    {
        lock (gate)
        {
            latest.TryGetValue(value.SegmentId, out var previous);
            if (value.Kind == "Committed" && value.DurationMs > (previous?.DurationMs ?? 0)
                && value.CommittedBytes > (previous?.CommittedBytes ?? 0))
            {
                inactivity.ReportActivity();
                mediaReceived.TrySetResult();
            }
            latest[value.SegmentId] = value;
            sink?.Report(value);
        }
    }

    /// <summary>场次发布后原子转发已合并的进度及后续回调。</summary>
    public void Activate(IProgress<MediaRecordingUpdate> value)
    {
        lock (gate)
        {
            if (sink is not null)
            {
                throw new InvalidOperationException("录制进度已关联场次。");
            }
            sink = value;
            foreach (var update in latest.Values)
            {
                sink.Report(update);
            }
        }
    }
}
