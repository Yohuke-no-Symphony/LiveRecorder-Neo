namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal static class OnlineAnalyticsProjection
{
    internal const double BUCKET_WIDTH_MS = 60000;

    internal static void Apply(AnalyticsReport report, double fromMs, double toMs, IEnumerable<OnlineAnalyticsInterval> projected)
    {
        report.FromMs = fromMs;
        report.ToMs = toMs;
        var intervals = new List<OnlineAnalyticsInterval>();
        foreach (var interval in projected.OrderBy(item => item.FromMs).ThenBy(item => item.ToMs))
        {
            var from = Math.Max(fromMs, Math.Max(interval.FromMs, intervals.Count > 0 ? intervals[^1].ToMs : fromMs));
            var to = Math.Min(toMs, interval.ToMs);
            if (to > from)
            {
                Append(intervals, from, to, interval.Count);
            }
        }
        report.Online = Summarize(intervals, toMs - fromMs);
        var buckets = report.Buckets.ToDictionary(bucket => bucket.OffsetMs);
        foreach (var interval in intervals)
        {
            var cursor = interval.FromMs;
            while (cursor < interval.ToMs)
            {
                var start = Math.Floor(cursor / BUCKET_WIDTH_MS) * BUCKET_WIDTH_MS;
                var end = Math.Min(interval.ToMs, start + BUCKET_WIDTH_MS);
                if (!buckets.TryGetValue(start, out var bucket))
                {
                    bucket = new() { OffsetMs = start };
                    buckets.Add(start, bucket);
                }
                var duration = end - cursor;
                var observed = bucket.OnlineObservedMs + duration;
                bucket.OnlineAverage = ((bucket.OnlineAverage ?? 0) * bucket.OnlineObservedMs + interval.Count * duration) / observed;
                bucket.OnlinePeak = Math.Max(bucket.OnlinePeak ?? 0, interval.Count);
                bucket.OnlineObservedMs = observed;
                cursor = end;
            }
        }
        foreach (var bucket in buckets.Values)
        {
            bucket.DurationMs = Math.Max(0, Math.Min(toMs, bucket.OffsetMs + BUCKET_WIDTH_MS) - Math.Max(fromMs, bucket.OffsetMs));
        }
        report.Buckets = buckets.Values.OrderBy(bucket => bucket.OffsetMs).ToList();
    }

    internal static OnlineAnalyticsReport Summarize(List<OnlineAnalyticsInterval> intervals, double rangeMs)
    {
        var observed = intervals.Sum(interval => interval.ToMs - interval.FromMs);
        return new()
        {
            Peak = intervals.Count > 0 ? intervals.Max(interval => interval.Count) : null,
            Average = observed > 0 ? intervals.Sum(interval => interval.Count * (interval.ToMs - interval.FromMs)) / observed : null,
            ObservedMs = observed,
            RangeMs = rangeMs,
            CoverageRate = rangeMs > 0 ? Math.Clamp(observed / rangeMs, 0, 1) : 0,
            Intervals = intervals
        };
    }

    internal static void Append(List<OnlineAnalyticsInterval> intervals, double from, double to, long count)
    {
        if (intervals.Count > 0 && intervals[^1].ToMs == from && intervals[^1].Count == count)
        {
            intervals[^1].ToMs = to;
        }
        else
        {
            intervals.Add(new() { FromMs = from, ToMs = to, Count = count });
        }
    }
}
