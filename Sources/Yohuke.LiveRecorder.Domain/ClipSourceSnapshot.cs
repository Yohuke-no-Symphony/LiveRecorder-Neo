namespace Yohuke.LiveRecorder.Domain;

/// <summary>Retains the physical source identity behind a bounded remote clipping input.</summary>
public sealed record ClipSourceSnapshot
{
    public string Id { get; init; } = "";
    public string SegmentId { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long Length { get; init; }
    public long CreationTicks { get; init; }
    public long WriteTicks { get; init; }
    public bool IsClosed { get; init; }
}
