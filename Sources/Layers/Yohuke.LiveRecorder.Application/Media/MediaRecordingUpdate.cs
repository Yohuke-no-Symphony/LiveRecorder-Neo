namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>文件提交位置与录制进度。</summary>
public sealed class MediaRecordingUpdate
{
    public string Kind { get; set; } = "Committed";
    public string SegmentId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string IndexRelativePath { get; set; } = "";
    public double StartOffsetMs { get; set; }
    public double DurationMs { get; set; }
    public long CommittedBytes { get; set; }
    public long IndexCommittedBytes { get; set; }
    public DateTime? LastMediaReceivedAtUtc { get; set; }
    public string Codec { get; set; } = "avc";
    public MediaRecordingCommitSnapshot? CommitSnapshot { get; set; }
}
