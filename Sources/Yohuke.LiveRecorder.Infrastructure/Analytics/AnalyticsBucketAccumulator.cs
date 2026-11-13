namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsBucketAccumulator(long bucketKey)
{
    internal AnalyticsBucket Value { get; } = new() { OffsetMs = bucketKey * 60000d };
    internal HashSet<string> UserIds { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> DanmuUserIds { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> PayingUserIds { get; } = new(StringComparer.Ordinal);
}
