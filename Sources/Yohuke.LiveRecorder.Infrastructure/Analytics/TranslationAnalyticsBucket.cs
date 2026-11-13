using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>一分钟内所有同传者的合计同传弹幕条数。</summary>
public sealed class TranslationAnalyticsBucket
{
    [JsonRequired]
    public double OffsetMs { get; set; }
    [JsonRequired]
    public int Count { get; set; }
}
