namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>不包含执行凭据的阶段图表与详情数据。</summary>
public sealed record TaskStageSummary
{
    public string Id { get; init; } = "";
    public string State { get; init; } = "Pending";
    public string? Substate { get; init; }
    public double? Progress { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public double? ElapsedMs { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<TaskMetric> Metrics { get; init; } = [];
}
