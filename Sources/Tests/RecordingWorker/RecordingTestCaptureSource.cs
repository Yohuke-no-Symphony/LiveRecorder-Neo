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
    internal DateTime LiveStartedAtUtc { get; set; } = DateTime.UnixEpoch;
    internal int Queries { get; private set; }
    private Func<RecordedEventEnvelope, CancellationToken, Task>? eventHandler;

    public Task<LiveRoomSnapshot> GetRoomAsync(RecordingAssignment assignment, CancellationToken cancellationToken)
    {
        Queries++;
        return Task.FromResult(new LiveRoomSnapshot(123, 456, "Recorder", "", "Broadcast", "", IsLive, LiveStartedAtUtc));
    }

    internal Task EmitAsync(string command, DateTime receivedAtUtc) => eventHandler!(new()
    {
        Kind = "RoomState", Command = command, Text = command, ReceivedAtUtc = receivedAtUtc
    }, CancellationToken.None);

    public Task<LiveStreamDescriptor> ResolveStreamAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        Task.FromResult(new LiveStreamDescriptor("https://capture.invalid/live.flv", new Dictionary<string, string>(), 10000, "avc", "flv"));

    public async Task SubscribeEventsAsync(RecordingAssignment assignment, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        var stopped = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await using var registration = onCancellation is null ? default : cancellationToken.Register(onCancellation);
        eventHandler = onEvent;
        EventsStarted.TrySetResult();
        try
        {
            await stopped;
        }
        finally
        {
            EventsStopped.TrySetResult();
        }
    }
}
