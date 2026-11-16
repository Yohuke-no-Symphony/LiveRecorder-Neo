using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed record RecordingUploadReceipt
{
    public Dictionary<string, RecordingCommitAcknowledgement> Segments { get; init; } = new(StringComparer.Ordinal);
    public RecordingEventAcknowledgement? Events { get; set; }
    public bool SessionClosed { get; set; }
}
