namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>Describes a committed, end-exclusive FLV byte range and its independent decoder configuration.</summary>
public sealed record IndexedFlvRangePlan
{
    public long SourceLength { get; init; }
    public long StartOffset { get; init; }
    public long EndOffset { get; init; }
    public long Length => EndOffset - StartOffset;
    public double StartMs { get; init; }
    public double EndMs { get; init; }
    public byte[] VideoHeader { get; init; } = [];
    public byte[]? AudioHeader { get; init; }
}
