namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsMoneyObservation
{
    internal string Category { get; set; } = "";
    internal string UserId { get; init; } = "";
    internal string UserName { get; init; } = "";
    internal long BucketKey { get; init; }
    internal decimal Amount { get; set; }
    internal bool UnknownAmount { get; set; }
    internal long GiftQuantity { get; set; }
    internal long UnknownGiftQuantity { get; set; }
    internal bool Unidentified { get; init; }
}
