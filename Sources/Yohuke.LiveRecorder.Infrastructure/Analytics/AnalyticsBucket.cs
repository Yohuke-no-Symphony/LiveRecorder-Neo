namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>一分钟内观测到的互动与付费流水。</summary>
public sealed class AnalyticsBucket
{
    public double OffsetMs { get; set; }
    public int Messages { get; set; }
    public decimal PaidAmount { get; set; }
}
