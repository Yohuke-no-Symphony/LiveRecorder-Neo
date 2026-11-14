using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>共享报告与独立任务生命周期的页面状态。</summary>
public sealed class AiAnalysisStatus
{
    public bool Enabled { get; set; }
    public bool CanGenerate { get; set; }
    public AiAnalysisJobSummary? CurrentJob { get; set; }
    public AiAnalysisResult? LatestCompleted { get; set; }
    public AiAnalysisResult? LatestPartial { get; set; }
    public bool Stale { get; set; }
    public bool LatestCompletedStale { get; set; }
    public bool LatestPartialStale { get; set; }
}
