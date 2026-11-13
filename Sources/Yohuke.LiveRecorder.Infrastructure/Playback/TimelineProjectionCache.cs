namespace Yohuke.LiveRecorder.Infrastructure.Playback;

internal sealed class TimelineProjectionCache
{
    public const int CURRENT_SCHEMA_VERSION = 1;
    public int SchemaVersion { get; set; } = CURRENT_SCHEMA_VERSION;
    public string SourceIdentity { get; set; } = "";
    public List<TimelineSourceState> Sources { get; set; } = [];
}
