namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>防止确认期间任务已重试而误清新执行的版本条件。</summary>
public sealed record TaskHistoryDeleteRequest(int Attempt, DateTime FinishedAtUtc);
