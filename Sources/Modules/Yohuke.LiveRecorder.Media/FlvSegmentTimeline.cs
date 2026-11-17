namespace Yohuke.LiveRecorder.Media;

internal sealed class FlvSegmentTimeline
{
    private uint? previousTimestamp;
    private long expandedTimestamp;

    internal long? FileOriginMs { get; private set; }

    internal long Expand(int timestamp)
    {
        var current = unchecked((uint)timestamp);
        expandedTimestamp = previousTimestamp is { } previous
            ? expandedTimestamp + unchecked((int)(current - previous)) : current;
        previousTimestamp = current;
        return expandedTimestamp;
    }

    internal void BeginFile(long originMs) => FileOriginMs = originMs;

    internal int ToFileTimestamp(long timestamp)
    {
        var local = timestamp - (FileOriginMs ?? timestamp);
        if (local is < 0 or > uint.MaxValue)
        {
            throw new InvalidDataException("The FLV segment timestamp exceeds the file timestamp range.");
        }
        return unchecked((int)(uint)local);
    }

    internal void Reset()
    {
        previousTimestamp = null;
        expandedTimestamp = 0;
        FileOriginMs = null;
    }
}
