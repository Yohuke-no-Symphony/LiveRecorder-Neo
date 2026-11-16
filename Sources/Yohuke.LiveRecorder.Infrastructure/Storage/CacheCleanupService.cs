using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>协调固定类别的可重建缓存清理，使磁盘统计在部分完成或取消后同样失效。</summary>
public sealed class CacheCleanupService(PlaybackCache playback, ArtworkCache artwork,
    AnalyticsContributionService contributions, StorageUsageService storage) : IDisposable
{
    private const int MAXIMUM_UNUSED_MINUTES = 365 * 24 * 60;
    private readonly SemaphoreSlim operations = new(1, 1);

    /// <summary>按类别与零至一年的最短未使用时间清理缓存，零表示所有不活跃缓存。</summary>
    public async Task<CacheCleanupResult> CleanupAsync(IReadOnlyCollection<string>? categories, int minimumUnusedMinutes,
        CancellationToken cancellationToken = default)
    {
        if (categories is null || categories.Count == 0 || minimumUnusedMinutes is < 0 or > MAXIMUM_UNUSED_MINUTES
            || categories.Any(category => category is not (CacheCleanupCategories.PLAYBACK
                or CacheCleanupCategories.ARTWORK or CacheCleanupCategories.ANALYTICS_CONTRIBUTIONS)))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        await operations.WaitAsync(cancellationToken);
        try
        {
            var results = new List<CacheCleanupCategoryResult>();
            foreach (var category in categories.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(category switch
                {
                    CacheCleanupCategories.PLAYBACK => await playback.CleanupAsync(minimumUnusedMinutes, cancellationToken),
                    CacheCleanupCategories.ARTWORK => await artwork.CleanupAsync(minimumUnusedMinutes, cancellationToken),
                    _ => await contributions.CleanupAsync(minimumUnusedMinutes, cancellationToken)
                });
            }
            return new() { Categories = results };
        }
        finally
        {
            storage.Invalidate();
            operations.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        operations.Dispose();
    }
}
