using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed record RecordingAssignmentState
{
    public RecordingAssignment Assignment { get; init; } = new();
    public RecordingSessionContinuation? LastContinuation { get; init; }
    public bool Released { get; init; }
    public List<string> CompletedSessionIds { get; init; } = [];
}
