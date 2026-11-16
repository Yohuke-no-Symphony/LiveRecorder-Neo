namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>Commits a matching media, index and immutable FLV prefix snapshot without embedding blob bytes.</summary>
public sealed record RecordingSegmentCommit
{
    public string SessionId { get; init; } = "";
    public string SegmentId { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string IndexRelativePath { get; init; } = "";
    public long CommitSequence { get; init; }
    public long MediaCommittedBytes { get; init; }
    public long IndexCommittedBytes { get; init; }
    public int PrefixLength { get; init; }
    public string PrefixSha256 { get; init; } = "";
    public double StartOffsetMs { get; init; }
    public double DurationMs { get; init; }
    public string Codec { get; init; } = "avc";
    public bool IsClosed { get; init; }
}
