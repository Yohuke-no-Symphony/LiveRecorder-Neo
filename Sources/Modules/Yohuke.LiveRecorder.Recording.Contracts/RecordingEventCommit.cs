namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Publishes complete event lines with an immutable index snapshot that can replace a previous partial block.</summary>
public sealed record RecordingEventCommit
{
    public long CommittedBytes { get; init; }
    public long IndexCommittedBytes { get; init; }
    public long ThroughSequence { get; init; }
    public string IndexSha256 { get; init; } = "";
    public bool IsClosed { get; init; }
}
