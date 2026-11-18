using System.Text.Json.Serialization;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>不包含服务密钥和归档路径的任务进度。</summary>
public sealed record AiAnalysisJobSummary(string Id, string SessionId, string State, double Progress,
    long PromptTokens, long CompletionTokens, long EstimatedTokens, int TokenBudget,
    int ModelRequests, int ToolCalls, string? ErrorCode, DateTime CreatedAtUtc, DateTime? FinishedAtUtc)
{
    public string? Error => AiErrors.Describe(ErrorCode);
    public string Title { get; init; } = "";
    public string Purpose { get; init; } = AiJobPurposes.ANALYSIS;
    public string? LiveMarkerFileId { get; init; }
    public bool CancelRequested { get; init; }
    public string Phase { get; init; } = "Preparing";
    public int TotalSegments { get; init; }
    public int CompletedSegments { get; init; }
    public int? CurrentSegment { get; init; }
    public double? CurrentSegmentStartMs { get; init; }
    public double? CurrentSegmentEndMs { get; init; }
    public int ContextCompactions { get; init; }
    public int ProgressRecoveries { get; init; }
    public int RequestRetries { get; init; }
    public string? WorkerId { get; init; }
    public int Attempt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TaskStageSummary>? Stages { get; init; }

    /// <summary>投影可公开给统计查看者的作业状态。</summary>
    public static AiAnalysisJobSummary From(AiAnalysisJob job) => new(job.Id, job.SessionId, job.State,
        job.Progress, job.PromptTokens, job.CompletionTokens, job.EstimatedTokens, job.TokenBudget,
        job.ModelRequests, job.ToolCalls, job.ErrorCode, job.CreatedAtUtc, job.FinishedAtUtc)
    {
        Title = job.Snapshot.Title,
        Purpose = job.Purpose,
        LiveMarkerFileId = job.LiveMarkerFileId,
        CancelRequested = job.CancelRequested,
        Phase = job.Context.Phase,
        TotalSegments = job.Context.Segments.Count,
        CompletedSegments = job.Context.Segments.Count(item => item.State is "Completed" or "ReviewedNoTopics" or "Empty"),
        CurrentSegment = job.Context.Phase == "Analyzing" && job.Context.CurrentSegmentIndex < job.Context.Segments.Count ? job.Context.CurrentSegmentIndex + 1 : null,
        CurrentSegmentStartMs = Current(job)?.StartMs,
        CurrentSegmentEndMs = Current(job)?.EndMs,
        ContextCompactions = job.Context.ContextCompactions,
        ProgressRecoveries = job.Context.ProgressRecoveries,
        RequestRetries = job.RequestRetries,
        WorkerId = job.WorkerId,
        Attempt = job.Attempt
    };

    private static AiAnalysisSegment? Current(AiAnalysisJob job) => job.Context.Phase == "Analyzing"
        ? job.Context.Segments.ElementAtOrDefault(job.Context.CurrentSegmentIndex) : null;
}
