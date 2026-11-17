namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>观测到的用户互动汇总。</summary>
public sealed class AnalyticsUser
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Messages { get; set; }
    public decimal PaidAmount { get; set; }
    public int DanmuCount { get; set; }
    public decimal SuperChatAmount { get; set; }
    public int SuperChatCount { get; set; }
    public decimal GiftAmount { get; set; }
    public long GiftQuantity { get; set; }
    public decimal GuardAmount { get; set; }
    public long GuardQuantity { get; set; }
    public decimal? RedPocketAmount { get; set; }
    public int RedPocketCount { get; set; }
}
