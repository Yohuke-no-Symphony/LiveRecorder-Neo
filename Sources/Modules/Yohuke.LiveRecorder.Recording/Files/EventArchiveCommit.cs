namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>Describes complete, flushed event lines and the partial index block active at that boundary.</summary>
public sealed record EventArchiveCommit
{
    public long CommittedBytes { get; init; }
    public long IndexCommittedBytes { get; init; }
    public long ThroughSequence { get; init; }
    public EventIndexBlock? PendingBlock { get; init; }
    public bool IsClosed { get; init; }
}
