using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Capture;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingLogFailureSource(string failure) : IRecordingCaptureSource
{
    private readonly RecordingTestCaptureSource source = new();

    public Task<LiveRoomSnapshot> GetRoomAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        failure == "Room" ? Task.FromException<LiveRoomSnapshot>(new IOException("Room unavailable."))
            : source.GetRoomAsync(assignment, cancellationToken);

    public Task<LiveStreamDescriptor> ResolveStreamAsync(RecordingAssignment assignment, CancellationToken cancellationToken) =>
        failure == "Stream" ? Task.FromException<LiveStreamDescriptor>(new IOException("Stream unavailable."))
            : source.ResolveStreamAsync(assignment, cancellationToken);

    public Task SubscribeEventsAsync(RecordingAssignment assignment, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken) => failure == "Events" ? Task.FromException(new IOException("Events unavailable."))
            : source.SubscribeEventsAsync(assignment, onEvent, cancellationToken);
}
