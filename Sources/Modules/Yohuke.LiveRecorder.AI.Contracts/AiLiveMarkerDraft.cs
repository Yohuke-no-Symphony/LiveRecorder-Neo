namespace Yohuke.LiveRecorder.Domain;

/// <summary>由本次独立生成任务提交、使用已提供证据定位的直播记录草稿。</summary>
public sealed class AiLiveMarkerDraft
{
    public string Id { get; set; } = "";
    public string Message { get; set; } = "";
    public double OffsetMs { get; set; }
    public string AnchorEvidenceId { get; set; } = "";
    public List<AiEvidence> Evidence { get; set; } = [];
}
