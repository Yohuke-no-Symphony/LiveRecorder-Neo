namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>清理确认时筛选范围内、截止时间之前的成功记录。</summary>
public sealed record TaskHistoryClearRequest(TaskHistoryQuery Filter, DateTime CompletedBeforeUtc);
