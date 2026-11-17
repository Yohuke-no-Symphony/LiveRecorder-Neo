using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>在线人数保持有效的半开时间区间，时间为录像的绝对偏移。</summary>
public sealed class OnlineAnalyticsInterval
{
    [JsonRequired]
    public double FromMs { get; set; }
    [JsonRequired]
    public double ToMs { get; set; }
    [JsonRequired]
    public long Count { get; set; }
}
