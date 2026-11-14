using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>有效观测区间内的在线峰值、时间加权平均和数据覆盖率。</summary>
public sealed class OnlineAnalyticsReport
{
    [JsonRequired]
    public long? Peak { get; set; }
    [JsonRequired]
    public double? Average { get; set; }
    [JsonRequired]
    public double ObservedMs { get; set; }
    [JsonRequired]
    public double RangeMs { get; set; }
    [JsonRequired]
    public double CoverageRate { get; set; }
    [JsonRequired]
    public List<OnlineAnalyticsInterval> Intervals { get; set; } = [];
}
