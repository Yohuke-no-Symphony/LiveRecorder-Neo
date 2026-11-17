namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>用户赠送的同一种礼物或同一大航海等级的项目合计。</summary>
public sealed class AnalyticsContributionProject
{
    public string GiftId { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int? GuardLevel { get; set; }
    public string? CoinType { get; set; }
    public string Text { get; set; } = "";
    public int Count { get; set; }
    public long Quantity { get; set; }
    public decimal? Amount { get; set; }
    public decimal KnownAmount { get; set; }
    public decimal QuotedAmount { get; set; }
    public int UnknownAmountCount { get; set; }
}
