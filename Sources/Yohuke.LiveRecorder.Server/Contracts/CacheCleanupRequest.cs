namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>按固定类别和最短未使用时间请求管理员缓存清理。</summary>
public sealed class CacheCleanupRequest
{
    public List<string> Categories { get; set; } = [];
    public int MinimumUnusedMinutes { get; set; }
}
