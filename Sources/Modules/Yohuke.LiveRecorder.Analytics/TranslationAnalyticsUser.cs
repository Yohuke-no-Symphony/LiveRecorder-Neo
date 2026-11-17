using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>同传者身份及去重后的同传弹幕条数。</summary>
public sealed class TranslationAnalyticsUser
{
    [JsonRequired]
    public string UserId { get; set; } = "";
    [JsonRequired]
    public string UserName { get; set; } = "";
    [JsonRequired]
    public int Count { get; set; }
}
