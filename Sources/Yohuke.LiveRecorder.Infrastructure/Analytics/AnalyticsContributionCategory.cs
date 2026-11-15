namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>单个用户的一类赠送小计，红包电池与支付观测分开表示。</summary>
public sealed class AnalyticsContributionCategory
{
    public string Kind { get; set; } = "";
    public int Count { get; set; }
    public long Quantity { get; set; }
    public decimal? Amount { get; set; }
    public decimal KnownAmount { get; set; }
    public decimal QuotedAmount { get; set; }
    public int UnknownAmountCount { get; set; }
    public decimal? TotalBattery { get; set; }
    public List<AnalyticsContributionProject> Projects { get; set; } = [];
}
