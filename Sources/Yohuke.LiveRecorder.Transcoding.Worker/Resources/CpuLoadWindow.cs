namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

internal sealed class CpuLoadWindow
{
    private readonly Lock sync = new();
    private readonly Queue<CpuObservation> samples = new();
    private string? error;

    internal void Add(TimeSpan timestamp, CpuCounters? counters, string? reason = null)
    {
        lock (sync)
        {
            if (counters is null || counters.Total <= 0 || counters.EffectiveProcessors <= 0)
            {
                samples.Clear();
                error = reason ?? "CPU 指标不可用";
                return;
            }
            var previous = samples.LastOrDefault();
            if (previous is not null && (timestamp <= previous.Time || timestamp - previous.Time > TimeSpan.FromSeconds(3)
                || previous.Counters.ResourceIdentity != counters.ResourceIdentity
                || previous.Counters.EffectiveProcessors != counters.EffectiveProcessors
                || counters.Total < previous.Counters.Total || counters.Busy < previous.Counters.Busy
                || counters.QuotaUsedSeconds < previous.Counters.QuotaUsedSeconds))
            {
                samples.Clear();
            }
            error = null;
            samples.Enqueue(new(timestamp, counters));
            while (samples.Count > 602)
            {
                samples.Dequeue();
            }
        }
    }

    internal CpuLoadSnapshot Read(TimeSpan now, int windowSeconds)
    {
        lock (sync)
        {
            var latest = samples.LastOrDefault();
            if (latest is null || now < latest.Time || now - latest.Time > TimeSpan.FromSeconds(3))
            {
                return CpuLoadSnapshot.Unknown(error ?? "CPU 指标已过期");
            }
            var boundary = latest.Time - TimeSpan.FromSeconds(windowSeconds);
            var first = samples.LastOrDefault(sample => sample.Time <= boundary);
            if (first is null)
            {
                return new(null, latest.Counters.EffectiveProcessors, "正在收集完整 CPU 采样窗口");
            }
            var total = latest.Counters.Total - first.Counters.Total;
            if (total <= 0)
            {
                return CpuLoadSnapshot.Unknown("CPU 计数器没有前进");
            }
            var ratio = (latest.Counters.Busy - first.Counters.Busy) / total;
            if (latest.Counters.QuotaUsedSeconds is { } used && first.Counters.QuotaUsedSeconds is { } previousUsed)
            {
                var quotaRatio = (used - previousUsed) / ((latest.Time - first.Time).TotalSeconds * latest.Counters.EffectiveProcessors);
                ratio = Math.Max(ratio, quotaRatio);
            }
            return new(Math.Clamp(ratio * 100, 0, 100), latest.Counters.EffectiveProcessors, null);
        }
    }
}
