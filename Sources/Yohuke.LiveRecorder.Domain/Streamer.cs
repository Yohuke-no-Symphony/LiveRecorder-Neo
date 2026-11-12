namespace Yohuke.LiveRecorder.Domain;

/// <summary>主播资料及持续录制目标。</summary>
public sealed class Streamer : Entity
{
    public long Uid { get; set; }
    public long RoomId { get; set; }
    public string Name { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string? AvatarRelativePath { get; set; }
    public string DirectoryName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IsMonitored { get; set; } = true;
    public string? AccountId { get; set; }
    public int? Quality { get; set; }
    public bool? RecordDanmu { get; set; }
    public long? SegmentBytes { get; set; }
    public bool IsLive { get; set; }
    public string State { get; set; } = "Waiting";
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public List<string> ErrorArguments { get; set; } = [];
    public DateTime? LastCheckedAtUtc { get; set; }
}
