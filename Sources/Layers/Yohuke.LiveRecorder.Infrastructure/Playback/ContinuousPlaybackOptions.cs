namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>限制连续播放索引计划的内存占用与保留时间。</summary>
public sealed class ContinuousPlaybackOptions
{
    public long PlanCacheLimitBytes { get; set; } = 64 * 1024 * 1024;
    public TimeSpan PlanCacheSlidingExpiration { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan PlanCacheAbsoluteExpiration { get; set; } = TimeSpan.FromHours(1);
}
