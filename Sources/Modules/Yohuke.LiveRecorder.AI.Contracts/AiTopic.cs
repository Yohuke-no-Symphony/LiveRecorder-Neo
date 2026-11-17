namespace Yohuke.LiveRecorder.Domain;

/// <summary>经来源校验的话题区间及文本依据。</summary>
public sealed class AiTopic
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public string Observations { get; set; } = "";
    public List<AiEvidence> Evidence { get; set; } = [];
}
