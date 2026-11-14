namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed record OnlineAnalyticsObservation(double OffsetMs, long Sequence, long? Count);
