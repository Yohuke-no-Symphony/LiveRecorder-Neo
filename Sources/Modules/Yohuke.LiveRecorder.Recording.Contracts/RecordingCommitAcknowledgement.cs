namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Confirms only the coherent media and index watermark visible on the server.</summary>
public sealed record RecordingCommitAcknowledgement
{
    public long CommitSequence { get; init; }
    public long MediaCommittedBytes { get; init; }
    public long IndexCommittedBytes { get; init; }
    public bool IsClosed { get; init; }
}
