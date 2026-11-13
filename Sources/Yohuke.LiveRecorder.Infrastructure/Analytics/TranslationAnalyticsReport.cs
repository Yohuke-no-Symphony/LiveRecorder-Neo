using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>从互动中独立统计的同传弹幕总量、译者与分钟分布。</summary>
public sealed class TranslationAnalyticsReport
{
    [JsonRequired]
    public int Count { get; set; }
    [JsonRequired]
    public int Users { get; set; }
    [JsonRequired]
    public List<TranslationAnalyticsUser> TopUsers { get; set; } = [];
    [JsonRequired]
    public List<TranslationAnalyticsBucket> Buckets { get; set; } = [];
}
