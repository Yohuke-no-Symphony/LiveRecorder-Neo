namespace Yohuke.LiveRecorder.Media;

internal sealed class FlvIndexEntry
{
    public string Kind { get; set; } = "";
    public long Offset { get; set; }
    public uint Size { get; set; }
    public double TimestampMs { get; set; }
    public int? OriginalTimestampMs { get; set; }
    public long? SourceTagIndex { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public byte[]? HeaderData { get; set; }
    public double SessionOffsetMs { get; set; }
}
