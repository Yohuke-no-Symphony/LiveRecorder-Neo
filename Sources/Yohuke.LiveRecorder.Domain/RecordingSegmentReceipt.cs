using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>Persists the coherent published watermark of a remotely uploaded media segment.</summary>
public sealed class RecordingSegmentReceipt : Entity
{
    public string AssignmentId { get; set; } = "";
    public int Generation { get; set; }
    public string SessionId { get; set; } = "";
    public RecordingSegmentCommit? Commit { get; set; }
    public bool Retired { get; set; }
}
