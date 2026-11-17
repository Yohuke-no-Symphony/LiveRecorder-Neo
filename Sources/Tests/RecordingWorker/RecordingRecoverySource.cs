using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Capture;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingRecoverySource : IRecordingCaptureSource
{
    public Task<LiveRoomSnapshot> GetRoomAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        Task.FromResult(new LiveRoomSnapshot(assignment.RoomId, 901, "Recovery", "", "Crash recovery", "", true, DateTime.UnixEpoch.AddSeconds(1)));

    public Task<LiveStreamDescriptor> ResolveStreamAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        Task.FromResult(new LiveStreamDescriptor("https://recovery.invalid/live.flv", new Dictionary<string, string>(), 10000, "avc", "flv"));

    public Task SubscribeEventsAsync(RecordingAssignment assignment, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}
