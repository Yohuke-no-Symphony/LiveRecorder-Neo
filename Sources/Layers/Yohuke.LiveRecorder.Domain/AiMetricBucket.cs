namespace Yohuke.LiveRecorder.Domain;

/// <summary>由本地归档计算的一分钟观测摘要。</summary>
public sealed class AiMetricBucket
{
    public double OffsetMs { get; set; }
    public int DanmuCount { get; set; }
    public int TranslationCount { get; set; }
    public int SuperChatCount { get; set; }
    public decimal SuperChatAmount { get; set; }
    public double DurationMs { get; set; }
    public double? OnlineAverage { get; set; }
    public long? OnlinePeak { get; set; }
    public double OnlineObservedMs { get; set; }
}
