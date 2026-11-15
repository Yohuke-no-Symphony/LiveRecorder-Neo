namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>选定种类和时间范围内的全部赠送观测，不受明细分页限制。</summary>
public sealed class AnalyticsContributionSummary
{
    public int Count { get; set; }
    public int Users { get; set; }
    public long Quantity { get; set; }
    public decimal Amount { get; set; }
    public decimal QuotedAmount { get; set; }
    public int UnknownAmountCount { get; set; }
    public decimal? TotalBattery { get; set; }
}
