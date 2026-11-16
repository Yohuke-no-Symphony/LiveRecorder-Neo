using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Capture;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class RecordingRuntimeTestSource : IRecordingCaptureSource
{
    private readonly DateTime liveStartedAtUtc = DateTime.UtcNow.AddMinutes(-1);
    private long eventId;

    public Task<LiveRoomSnapshot> GetRoomAsync(RecordingAssignment assignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LiveRoomSnapshot(assignment.RoomId, 901, "Worker HTTP 测试", "", "远端录像中播放", "", true, liveStartedAtUtc));
    }

    public Task<LiveStreamDescriptor> ResolveStreamAsync(RecordingAssignment assignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LiveStreamDescriptor("https://recording-fixture.invalid/live.flv", new Dictionary<string, string>(), 10000, "avc", "flv"));
    }

    public async Task SubscribeEventsAsync(RecordingAssignment assignment, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            await onEvent(new()
            {
                EventId = "recording-http-" + Interlocked.Increment(ref eventId),
                ReceivedAtUtc = now,
                SourceTimeUtc = now,
                UserId = "901",
                UserName = "远端观众",
                Text = "录像 Worker 弹幕",
                Command = "DANMU_MSG"
            }, cancellationToken);
            await Task.Delay(100, cancellationToken);
        }
    }
}
