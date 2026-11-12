namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>观测到的用户互动汇总。</summary>
public sealed class AnalyticsUser
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Messages { get; set; }
    public decimal PaidAmount { get; set; }
}
