namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>导出范围内的一段媒体或真实断流空档。</summary>
public sealed class MediaInputSpan
{
    public string? Path { get; set; }
    public double SourceStartMs { get; set; }
    public double DurationMs { get; set; }
    public double SessionStartMs { get; set; }
    public IndexedFlvRangePlan? Range { get; set; }
    public bool IsGap => Path is null;
}
