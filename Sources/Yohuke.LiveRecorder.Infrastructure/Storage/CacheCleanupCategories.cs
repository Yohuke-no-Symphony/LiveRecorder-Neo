namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>允许管理员清理的可重建缓存类别。</summary>
public static class CacheCleanupCategories
{
    public const string PLAYBACK = "playback";
    public const string ARTWORK = "artwork";
    public const string ANALYTICS_CONTRIBUTIONS = "analyticsContributions";
}
