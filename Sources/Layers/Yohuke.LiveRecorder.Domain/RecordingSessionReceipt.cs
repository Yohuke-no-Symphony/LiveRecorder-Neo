using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>Tracks a remotely captured session without exposing uncommitted media in the recording library.</summary>
public sealed class RecordingSessionReceipt : Entity
{
    public string AssignmentId { get; set; } = "";
    public int Generation { get; set; }
    public RecordingSessionDescriptor Descriptor { get; set; } = new();
    public string DirectoryPath { get; set; } = "";
    public bool Published { get; set; }
    public bool LibraryPublished { get; set; }
    public bool Retired { get; set; }
    public RecordingEventCommit? Events { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
}
