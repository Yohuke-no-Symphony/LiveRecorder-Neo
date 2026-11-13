namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>一分钟内观测到的互动与付费流水。</summary>
public sealed class AnalyticsBucket
{
    public double OffsetMs { get; set; }
    public int Messages { get; set; }
    public decimal PaidAmount { get; set; }
    public int DanmuCount { get; set; }
    public int ActiveUsers { get; set; }
    public int DanmuUsers { get; set; }
    public int PayingUsers { get; set; }
    public decimal SuperChatAmount { get; set; }
    public decimal GiftAmount { get; set; }
    public decimal GuardAmount { get; set; }
}
