namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>个人与管理员任务页面共用的公开进度投影。</summary>
public sealed record BackgroundTaskSummary
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Type { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = "Queued";
    public string Origin { get; init; } = "Legacy";
    public string? InitiatorUserId { get; init; }
    public string? InitiatorName { get; init; }
    public string? InitiatorAvatarUrl { get; init; }
    public string? WorkerId { get; init; }
    public string? WorkerName { get; init; }
    public bool? WorkerOnline { get; init; }
    public string? ProfileName { get; init; }
    public string? Mode { get; init; }
    public string? LiveMarkerFileId { get; init; }
    public double Progress { get; init; }
    public int Attempt { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public DateTime? LastUpdatedAtUtc { get; init; }
    public string? Error { get; init; }
    public bool CancelRequested { get; init; }
    public bool CanCancel { get; init; }
    public bool CanRetry { get; init; }
    public bool CanInterruptRecording { get; init; }
    public bool CanDelete { get; init; }
    public bool CanViewResults { get; init; }
    public int OutputCount { get; init; }
    public IReadOnlyList<TaskStageSummary> Stages { get; init; } = [];
}
