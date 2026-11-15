using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsMoneyObservation
{
    internal string Kind { get; init; } = "";
    internal string Identity { get; init; } = "";
    internal AnalyticsMoneyCategory Category { get; init; }
    internal string UserId { get; set; } = "";
    internal string UserName { get; set; } = "";
    internal long BucketKey { get; init; }
    internal decimal Amount { get; set; }
    internal bool UnknownAmount { get; set; }
    internal long GiftQuantity { get; set; }
    internal long Quantity { get; set; }
    internal long UnknownGiftQuantity { get; set; }
    internal bool Unidentified { get; init; }
    internal RecordedEventEnvelope? SourceEvent { get; set; }
}
