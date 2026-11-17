namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>经过权限与历史隐藏过滤的统一任务页。</summary>
public sealed record TaskHistoryPage(IReadOnlyList<BackgroundTaskSummary> Items, long Total, string? NextCursor);
