using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>提供可重复读取的固定事件快照及其在逻辑场次内的投影范围。</summary>
public sealed record AnalyticsProjectionSource
{
    /// <summary>从来源局部时间转换到逻辑场次时间的偏移。</summary>
    public double OffsetMs { get; init; }

    /// <summary>包含的来源局部起点。</summary>
    public double FromMs { get; init; }

    /// <summary>排除的来源局部终点，空值表示未指定终点。</summary>
    public double? ToMs { get; init; }

    /// <summary>每次枚举均读取同一快照，允许在投影前完整恢复裁切基线。</summary>
    public required Func<CancellationToken, IAsyncEnumerable<RecordedEventEnvelope>> ReadAsync { get; init; }
}
