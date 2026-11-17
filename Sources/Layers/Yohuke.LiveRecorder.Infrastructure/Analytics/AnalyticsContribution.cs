namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>业务去重后的赠送明细，或同一用户同一赠送项目的合计。</summary>
public sealed class AnalyticsContribution
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public double OffsetMs { get; set; }
    public long Sequence { get; set; }
    public string Text { get; set; } = "";
    public string GiftId { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int? GuardLevel { get; set; }
    public long Quantity { get; set; }
    public int Count { get; set; }
    public decimal? Amount { get; set; }
    public decimal? QuotedAmount { get; set; }
    public string? CoinType { get; set; }
    public string PriceSource { get; set; } = "";
    public decimal? TotalBattery { get; set; }
    public AnalyticsContributionEventDetails? Event { get; set; }
    public decimal KnownAmount { get; set; }
    public int UnknownAmountCount { get; set; }
    public List<AnalyticsContributionCategory> Categories { get; set; } = [];
}
