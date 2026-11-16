namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Confirms the event and index boundaries safely available for replay.</summary>
public sealed record RecordingEventAcknowledgement
{
    public long CommittedBytes { get; init; }
    public long IndexCommittedBytes { get; init; }
    public long ThroughSequence { get; init; }
    public bool IsClosed { get; init; }
}
