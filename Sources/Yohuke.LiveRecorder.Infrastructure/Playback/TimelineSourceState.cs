namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal sealed class TimelineSourceState
{
    public long Position { get; set; }
    public long ObservedLength { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public long LastSequence { get; set; }
    public string PrefixHash { get; set; } = "";
    public string TailHash { get; set; } = "";
    public Dictionary<long, long> Heat { get; set; } = [];
    public Dictionary<string, TimelineMarkerState> Markers { get; set; } = new(StringComparer.Ordinal);
}
