namespace Yohuke.LiveRecorder.Domain;

/// <summary>固定时间分段的证据数量与独立审阅进度。</summary>
public sealed class AiAnalysisSegment
{
    public int Index { get; set; }
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public int EvidenceCount { get; set; }
    public string State { get; set; } = "Pending";
    public string Overview { get; set; } = "";
    public List<string> RequiredEvidenceIds { get; set; } = [];
    public List<string> TopicIds { get; set; } = [];
    public List<string> LiveMarkerIds { get; set; } = [];
}
