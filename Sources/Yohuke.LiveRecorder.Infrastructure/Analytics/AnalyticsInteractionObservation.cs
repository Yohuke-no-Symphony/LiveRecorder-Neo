namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsInteractionObservation
{
    internal string Kind { get; init; } = "";
    internal bool IsTranslation { get; set; }
    internal string UserId { get; set; } = "";
    internal string UserName { get; set; } = "";
    internal long BucketKey { get; init; }
    internal decimal? RedPocketAmount { get; set; }
}
