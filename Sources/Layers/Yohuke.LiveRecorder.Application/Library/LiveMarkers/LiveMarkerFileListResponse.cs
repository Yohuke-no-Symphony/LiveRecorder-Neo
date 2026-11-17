namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>场次共享记录文件与上传时默认采用的来源时区。</summary>
public sealed record LiveMarkerFileListResponse(IReadOnlyList<LiveMarkerFileSummary> Files, string DefaultTimeZoneId);
