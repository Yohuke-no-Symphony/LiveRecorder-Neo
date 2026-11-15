using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsContributionSnapshot
{
    internal const int CURRENT_SCHEMA_VERSION = 2;
    [JsonRequired]
    public int SchemaVersion { get; set; } = CURRENT_SCHEMA_VERSION;
    [JsonRequired]
    public string SessionId { get; set; } = "";
    [JsonRequired]
    public string StorageRoot { get; set; } = "";
    [JsonRequired]
    public string Kind { get; set; } = "";
    [JsonRequired]
    public double FromMs { get; set; }
    [JsonRequired]
    public double ToMs { get; set; }
    [JsonRequired]
    public List<AnalyticsContribution> Events { get; init; } = [];
    [JsonRequired]
    public List<AnalyticsContribution> Users { get; init; } = [];
    [JsonRequired]
    public AnalyticsContributionSummary Summary { get; init; } = new();
}
