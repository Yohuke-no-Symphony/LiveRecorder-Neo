namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>确认录像任务中断时需要匹配的最后上报时间。</summary>
public sealed record RecordingTaskInterruptRequest(DateTime LastReportedAtUtc);
