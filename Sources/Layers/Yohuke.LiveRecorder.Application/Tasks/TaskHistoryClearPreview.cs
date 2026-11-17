namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>二次确认使用的成功任务数量与时间截止值。</summary>
public sealed record TaskHistoryClearPreview(long Count, DateTime CompletedBeforeUtc);
