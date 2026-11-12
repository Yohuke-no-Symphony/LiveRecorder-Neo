namespace Yohuke.LiveRecorder.Domain;

/// <summary>可重试并持有执行租约的持久后台作业。</summary>
public sealed class MediaJob : Entity
{
    public string OwnerId { get; set; } = "";
    public string Kind { get; set; } = "Clip";
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Mode { get; set; } = "Exact";
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public List<string> SegmentIds { get; set; } = [];
    public List<RecordingSegment> SourceSegments { get; set; } = [];
    public int SourceRevision { get; set; } = 1;
    public string? MutationKey { get; set; }
    public string State { get; set; } = "Queued";
    public double Progress { get; set; }
    public string? Error { get; set; }
    public string? OutputRelativePath { get; set; }
    public List<string> OutputRelativePaths { get; set; } = [];
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public int Attempt { get; set; }
    public bool CancelRequested { get; set; }
}
