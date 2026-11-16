using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Capture;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingTestCaptureSource(Action? onCancellation = null) : IRecordingCaptureSource
{
    internal TaskCompletionSource EventsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource EventsStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool IsLive { get; set; } = true;

    public Task<LiveRoomSnapshot> GetRoomAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        Task.FromResult(new LiveRoomSnapshot(123, 456, "Recorder", "", "Broadcast", "", IsLive, DateTime.UnixEpoch));

    public Task<LiveStreamDescriptor> ResolveStreamAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        Task.FromResult(new LiveStreamDescriptor("https://capture.invalid/live.flv", new Dictionary<string, string>(), 10000, "avc", "flv"));

    public async Task SubscribeEventsAsync(RecordingAssignment assignment, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        await using var registration = onCancellation is null ? default : cancellationToken.Register(onCancellation);
        EventsStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            EventsStopped.TrySetResult();
        }
    }
}
