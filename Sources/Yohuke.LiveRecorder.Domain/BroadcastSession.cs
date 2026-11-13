namespace Yohuke.LiveRecorder.Domain;

/// <summary>聚合重连与物理分段的一场录播。</summary>
public sealed class BroadcastSession : Entity
{
    public string StreamerId { get; set; } = "";
    public long RoomId { get; set; }
    public string Title { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string? CoverRelativePath { get; set; }
    public string DirectoryPath { get; set; } = "";
    public long LiveTime { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime LastMediaAtUtc { get; set; }
    public string State { get; set; } = "Recording";
    public bool HasDanmu { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public List<string> ErrorArguments { get; set; } = [];
    public bool IsTrashed { get; set; }
    public bool IsPermanent { get; set; }
    public DateTime? RetentionStartedAtUtc { get; set; }
    public int MediaRevision { get; set; } = 1;
    public string? MediaRevisionJobId { get; set; }
    public int MediaRevisionAttempt { get; set; }
    public bool IsHidden { get; set; }
    public List<string> GroupedSessionIds { get; set; } = [];
    public List<EventArchiveSource> EventSources { get; set; } = [];
    public bool ProjectionReady { get; set; } = true;
}
