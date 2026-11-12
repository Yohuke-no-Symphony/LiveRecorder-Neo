namespace Yohuke.LiveRecorder.Domain;

/// <summary>用户可在保留期内查看和下载的媒体产物。</summary>
public sealed class Clip : Entity
{
    public string OwnerId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string Title { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Bytes { get; set; }
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsTrashed { get; set; }
    public int PublicationAttempt { get; set; }
}
