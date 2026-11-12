namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>从文件计算的协议支付与金币面值观察，不代表用户实际花费或主播结算收入。</summary>
public sealed class AnalyticsReport
{
    public const int CURRENT_SCHEMA_VERSION = 2;
    public int SchemaVersion { get; set; }
    public long SourceBytes { get; set; }
    public string SourceIdentity { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public DateTime BuiltAtUtc { get; set; }
    public decimal ObservedAmount { get; set; }
    /// <summary>兼容旧接口的观测总额名称；仅等于金币面值加协议支付，不含报价。</summary>
    public decimal PaidRevenue { get => ObservedAmount; set => ObservedAmount = value; }
    public decimal GoldGiftValue { get; set; }
    public decimal ProtocolPaidAmount { get; set; }
    public decimal QuotedAmount { get; set; }
    public int UnknownAmountCount { get; set; }
    public long UnknownGiftQuantity { get; set; }
    public int UnidentifiedFinancialEvents { get; set; }
    public long GiftQuantity { get; set; }
    public int ActiveUsers { get; set; }
    public int DanmuCount { get; set; }
    public int GiftCount { get; set; }
    public int SuperChatCount { get; set; }
    public int GuardCount { get; set; }
    public int RedPocketCount { get; set; }
    public int CoverageGaps { get; set; }
    public List<AnalyticsUser> TopUsers { get; set; } = [];
    public List<AnalyticsBucket> Buckets { get; set; } = [];
}
