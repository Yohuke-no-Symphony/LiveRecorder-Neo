namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsBuildData
{
    internal AnalyticsReport Report { get; init; } = new();
    internal AnalyticsContributionSnapshot? Contributions { get; init; }
}
