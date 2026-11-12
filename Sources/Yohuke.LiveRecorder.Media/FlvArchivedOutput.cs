namespace Yohuke.LiveRecorder.Media;

internal sealed class FlvArchivedOutput
{
    internal string Path { get; init; } = "";
    internal string IndexPath { get; init; } = "";
    internal double StartOffsetMs { get; init; }
    internal double DurationMs { get; init; }
}
