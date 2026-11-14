namespace Yohuke.LiveRecorder.Domain;

/// <summary>全场共享的增量或完整 AI 复盘结果。</summary>
public sealed class AiAnalysisResult
{
    public string JobId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Purpose { get; set; } = AiJobPurposes.ANALYSIS;
    public string? LiveMarkerFileId { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public bool IsComplete { get; set; }
    public string Overview { get; set; } = "";
    public List<AiTopic> Topics { get; set; } = [];
    public List<AiLiveMarkerDraft> LiveMarkers { get; set; } = [];
}
