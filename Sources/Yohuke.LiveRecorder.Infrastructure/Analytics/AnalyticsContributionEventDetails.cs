using System.Text.Json.Nodes;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>用于总结明细展示的事件内容，保留累计数量精度并排除原始协议包。</summary>
public sealed class AnalyticsContributionEventDetails
{
    public int SchemaVersion { get; set; } = 1;
    public long Sequence { get; set; }
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? SourceTimeUtc { get; set; }
    public double OffsetMs { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Text { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public decimal? Amount { get; set; }
    public long Quantity { get; set; }
    public string? TransactionId { get; set; }
    public string? CoinType { get; set; }
    public string PriceSource { get; set; } = "";
    public JsonObject? Payload { get; set; }
}
