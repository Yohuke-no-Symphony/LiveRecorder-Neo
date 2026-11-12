namespace Yohuke.LiveRecorder.Domain;

/// <summary>具有独立文件和时间映射的录制片段。</summary>
public sealed class RecordingSegment : Entity
{
    public string SessionId { get; set; } = "";
    public string AttemptId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public double StartOffsetMs { get; set; }
    public double DurationMs { get; set; }
    public long CommittedBytes { get; set; }
    public bool IsClosed { get; set; }
    public int Revision { get; set; } = 1;
    public string Codec { get; set; } = "avc";
    public string IndexRelativePath { get; set; } = "";
    public string? RootRelativePath { get; set; }
    public string? RootIndexRelativePath { get; set; }
}
