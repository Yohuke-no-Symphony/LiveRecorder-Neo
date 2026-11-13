namespace Yohuke.LiveRecorder.Application.Library.LiveMarkers;

/// <summary>可展示的记录文件及当前用户的管理能力。</summary>
public sealed record LiveMarkerFileSummary(
    string Id,
    string OriginalFileName,
    string UploaderId,
    string UploaderName,
    DateTime UploadedAtUtc,
    int EntryCount,
    long CalibrationMs,
    string SourceTimeZoneId,
    bool CanManage);
