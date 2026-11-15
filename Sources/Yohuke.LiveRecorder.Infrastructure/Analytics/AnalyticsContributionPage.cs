namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

/// <summary>可遍历全部赠送明细或用户项目合计的分页响应。</summary>
public sealed class AnalyticsContributionPage
{
    public string Kind { get; set; } = "";
    public string View { get; set; } = "events";
    public string SnapshotId { get; set; } = "";
    public double FromMs { get; set; }
    public double ToMs { get; set; }
    public List<AnalyticsContribution> Items { get; set; } = [];
    public int? NextCursor { get; set; }
    public int TotalCount { get; set; }
    public AnalyticsContributionSummary Summary { get; set; } = new();
}
