namespace Yohuke.LiveRecorder.Clipping.Contracts;

/// <summary>Defines an end-exclusive committed FLV range and the decoder headers needed to rebuild it.</summary>
public sealed record ClipSourceRange
{
    public long SourceLength { get; init; }
    public long StartOffset { get; init; }
    public long EndOffset { get; init; }
    public double StartMs { get; init; }
    public double EndMs { get; init; }
    public byte[] VideoHeader { get; init; } = [];
    public byte[]? AudioHeader { get; init; }
}
