namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>Durable media and index boundaries with an immutable copy of the mutable FLV prefix.</summary>
public sealed record MediaRecordingCommitSnapshot
{
    public string SegmentId { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string IndexRelativePath { get; init; } = "";
    public long CommitSequence { get; init; }
    public long MediaCommittedBytes { get; init; }
    public long IndexCommittedBytes { get; init; }
    public double StartOffsetMs { get; init; }
    public double DurationMs { get; init; }
    public string Codec { get; init; } = "avc";
    public int PrefixLength { get; init; }
    public string PrefixSha256 { get; init; } = "";
    public string PrefixRelativePath { get; init; } = "";
    public bool IsClosed { get; init; }
}
