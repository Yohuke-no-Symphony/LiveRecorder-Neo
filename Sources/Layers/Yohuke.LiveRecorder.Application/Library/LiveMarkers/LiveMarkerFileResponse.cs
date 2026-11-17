namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>所选记录文件的独立条目与时间起点。</summary>
public sealed record LiveMarkerFileResponse(
    LiveMarkerFileSummary File,
    string StartTime,
    IReadOnlyList<LiveMarkerEntryResponse> Entries);
