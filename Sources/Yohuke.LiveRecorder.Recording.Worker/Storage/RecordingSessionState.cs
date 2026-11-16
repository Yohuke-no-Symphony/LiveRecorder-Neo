using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed record RecordingSessionState
{
    public string AssignmentId { get; init; } = "";
    public RecordingSessionDescriptor Descriptor { get; set; } = new();
    public DateTime? LastCapturedAtUtc { get; set; }
    public RecordingEventCommit? Events { get; set; }
    public string? EventIndexPath { get; set; }
}
