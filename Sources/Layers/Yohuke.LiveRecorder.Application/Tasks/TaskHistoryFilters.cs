namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>管理员任务筛选使用的创建人和执行节点。</summary>
public sealed record TaskHistoryFilters(IReadOnlyList<TaskHistoryFilterOption> Initiators, IReadOnlyList<TaskHistoryFilterOption> Workers);
