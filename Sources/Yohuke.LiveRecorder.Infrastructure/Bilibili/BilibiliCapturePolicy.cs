namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>单连接补录的容量、节流与有限重试策略。</summary>
public sealed class BilibiliCapturePolicy
{
    public int QueueCapacity { get; init; } = 8192;
    public int MaxHistoryItems { get; init; } = 100;
    public int MaxTrackedRedPockets { get; init; } = 64;
    public int MaxRedPocketRequestsPerCycle { get; init; } = 2;
    public int MaxRedPocketAttempts { get; init; } = 6;
    public TimeSpan SchedulerInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MinimumHistoryInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RedPocketRetryInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TrackedRedPocketLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
