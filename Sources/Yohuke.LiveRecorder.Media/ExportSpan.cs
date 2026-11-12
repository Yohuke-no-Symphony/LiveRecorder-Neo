namespace Yohuke.LiveRecorder.Media;

internal sealed class ExportSpan
{
    internal string? Path { get; init; }
    internal double SourceStartMs { get; set; }
    internal double DurationMs { get; set; }
    internal double SessionStartMs { get; set; }
    internal MediaProbeDetails? Details { get; set; }
    internal FlvIndexSnapshot? Index { get; set; }
    internal FlvIndexEntry? FirstFrame { get; set; }
    internal long EndOffset { get; set; }
}
