namespace Yohuke.LiveRecorder.Domain.LiveMarkers;

/// <summary>磁盘记录文件的身份、上传归属、来源与独立校准快照。</summary>
public sealed record LiveMarkerFileMetadata
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string OriginId { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string UploaderId { get; init; } = "";
    public string UploaderName { get; init; } = "";
    public DateTime UploadedAtUtc { get; init; } = DateTime.UtcNow;
    public int EntryCount { get; init; }
    public long CalibrationMs { get; init; }
    public string SourceTimeZoneId { get; init; } = "";
    public string ContentHash { get; init; } = "";
}
