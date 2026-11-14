namespace Yohuke.LiveRecorder.Media;

internal sealed class IndexedPlaybackRange
{
    internal required FlvIndexEntry First { get; init; }
    internal required long EndOffset { get; init; }
    internal required double EndMs { get; init; }
    internal required double TimestampOriginMs { get; init; }
    internal required byte[] VideoHeader { get; init; }
    internal byte[]? AudioHeader { get; init; }
    internal bool ReachesCommittedEnd { get; init; }
}
