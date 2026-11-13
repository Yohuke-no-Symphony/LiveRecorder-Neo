namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>保留原记录并附加当前场次真实时间轴偏移的展示条目。</summary>
public sealed record LiveMarkerEntryResponse(
    int Index,
    string RealDateTime,
    string LiveTime,
    string Message,
    string MarkerColor,
    double OffsetMs);
