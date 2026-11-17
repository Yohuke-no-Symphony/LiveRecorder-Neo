using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Capture;

internal interface IRecordingCaptureSource
{
    Task<LiveRoomSnapshot> GetRoomAsync(RecordingAssignment assignment, CancellationToken cancellationToken);
    Task<LiveStreamDescriptor> ResolveStreamAsync(RecordingAssignment assignment, CancellationToken cancellationToken);
    Task SubscribeEventsAsync(RecordingAssignment assignment, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken);
}
