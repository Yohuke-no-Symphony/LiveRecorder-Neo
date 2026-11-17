namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>包含来源投影后的统计报告与文件适配层可用的贡献观测。</summary>
public sealed class AnalyticsProjectionResult
{
    internal AnalyticsProjectionResult(AnalyticsReport report, AnalyticsAccumulator accumulator)
    {
        Report = report;
        Accumulator = accumulator;
    }

    /// <summary>不包含文件缓存身份的统计报告。</summary>
    public AnalyticsReport Report { get; }

    internal AnalyticsAccumulator Accumulator { get; }
}
