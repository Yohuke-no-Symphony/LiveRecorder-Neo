namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>按 SC、礼物或大航海汇总流水、互动笔数、数量和参与用户。</summary>
public sealed class AnalyticsCategorySummary
{
    public string Kind { get; set; } = "";
    public decimal Amount { get; set; }
    public int Count { get; set; }
    public long Quantity { get; set; }
    public int Users { get; set; }
}
