namespace Yohuke.LiveRecorder.Domain;

/// <summary>来源固定、仅保留正文与录像时间的分析证据。</summary>
public sealed class AiEvidence
{
    public string Id { get; set; } = "";
    public double OffsetMs { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}
