namespace Yohuke.LiveRecorder.Domain.LiveMarkers;

/// <summary>一次解析后随场次保存的原生直播记录文档。</summary>
public sealed record LiveMarkerDocument
{
    public DateTimeOffset StartTime { get; init; }
    public IReadOnlyList<LiveMarkerEntry> Entries { get; init; } = [];
}
