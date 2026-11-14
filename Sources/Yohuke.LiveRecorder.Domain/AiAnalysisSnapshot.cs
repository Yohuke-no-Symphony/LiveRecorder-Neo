namespace Yohuke.LiveRecorder.Domain;

/// <summary>一次分析固定的数据来源、规则和精简摘要。</summary>
public sealed class AiAnalysisSnapshot
{
    public const int SEGMENT_DURATION_MS = 1800000;
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public double DurationMs { get; set; }
    public double RecordedDurationMs { get; set; }
    public double? OnlineAverage { get; set; }
    public long? OnlinePeak { get; set; }
    public double OnlineObservedMs { get; set; }
    public double OnlineCoverageRate { get; set; }
    public string TranslationPattern { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string EvidenceRelativePath { get; set; } = "";
    public int EvidenceCount { get; set; }
    public List<AiMetricBucket> Buckets { get; set; } = [];
    public List<AiAnalysisSegment> Segments { get; set; } = [];
}
