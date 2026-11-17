using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>从固定事件来源恢复裁切基线，按逻辑时间投影互动、金融及在线观测。</summary>
public sealed class AnalyticsProjectionEngine
{
    /// <summary>独立计算半开区间统计，不依赖文件缓存、数据库或来源事件实例的可变状态。</summary>
    public async Task<AnalyticsProjectionResult> BuildAsync(IReadOnlyList<AnalyticsProjectionSource> sources,
        AnalyticsProjectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        var accumulator = new AnalyticsAccumulator(options.CaptureContributions);
        var exportSequence = 0L;
        var onlineSources = new List<(OnlineAnalyticsAccumulator Accumulator, double Offset, double From, double? To)>();
        var fullEndMs = options.FixedEndMs ?? options.InitialEndMs;
        var requestedFrom = Math.Min(options.FromMs ?? 0, options.FixedEndMs ?? double.MaxValue);
        var requestedTo = Math.Min(options.ToMs ?? double.MaxValue, options.FixedEndMs ?? double.MaxValue);
        foreach (var source in sources)
        {
            var sourceFrom = Math.Max(source.FromMs, Math.Max(0, requestedFrom - source.OffsetMs));
            var sourceTo = Math.Min(source.ToMs ?? double.MaxValue, requestedTo - source.OffsetMs);
            var online = new OnlineAnalyticsAccumulator();
            onlineSources.Add((online, source.OffsetMs, sourceFrom, sourceTo));
            if (sourceFrom > 0)
            {
                await foreach (var item in source.ReadAsync(cancellationToken))
                {
                    if (item.OffsetMs >= 0 && item.OffsetMs < Math.Min(sourceFrom, source.ToMs ?? double.MaxValue))
                    {
                        accumulator.SeedBaseline(item);
                    }
                }
            }

            await foreach (var item in source.ReadAsync(cancellationToken))
            {
                if (options.FixedEndMs is null && item.OffsetMs >= source.FromMs &&
                    (source.ToMs is null || item.OffsetMs < source.ToMs) && item.OffsetMs + source.OffsetMs >= 0)
                {
                    fullEndMs = Math.Max(fullEndMs, item.OffsetMs + source.OffsetMs + 1);
                }
                if (item.OffsetMs < sourceTo)
                {
                    online.Add(item);
                }
                if (item.OffsetMs < sourceFrom || item.OffsetMs >= sourceTo)
                {
                    continue;
                }

                var projectedOffset = item.OffsetMs + source.OffsetMs;
                if (projectedOffset < requestedFrom || projectedOffset >= requestedTo)
                {
                    continue;
                }
                var projected = CopyForProjection(item, projectedOffset);
                if (options.CaptureContributions)
                {
                    projected.Sequence = checked(++exportSequence);
                }
                accumulator.Add(projected, projected.Kind == "Danmu" && options.TranslationPattern?.IsMatch(projected.Text) == true);
            }
        }

        var report = options.CaptureContributions ? accumulator.BuildForExport() : accumulator.Build();
        var rangeTo = Math.Min(requestedTo, fullEndMs);
        var rangeFrom = Math.Min(requestedFrom, rangeTo);
        var intervals = onlineSources.SelectMany(source =>
        {
            var end = Math.Min(source.To ?? double.MaxValue, rangeTo - source.Offset);
            return end > source.From ? source.Accumulator.Build(source.From, end, source.Offset).Intervals : [];
        });
        OnlineAnalyticsProjection.Apply(report, rangeFrom, rangeTo, intervals);
        return new(report, accumulator);
    }

    private static RecordedEventEnvelope CopyForProjection(RecordedEventEnvelope item, double offsetMs)
    {
        var projected = AnalyticsContributionEvent.Copy(item);
        projected.OffsetMs = offsetMs;
        projected.RawJson = item.RawJson;
        projected.TextCode = item.TextCode;
        projected.TextArguments = [.. item.TextArguments];
        return projected;
    }
}
