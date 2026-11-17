namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>批量读取节点卡片所需的活动任务，避免逐节点请求。</summary>
public sealed record TaskHistoryActiveRequest(IReadOnlyList<string> WorkerIds);
