using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>统一保护与回收播放窗口和连续分片的磁盘缓存。</summary>
public sealed class PlaybackCache(IAppPaths paths, ISettingsStore settingsStore) : IDisposable
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, DateTime> active = new();
    private readonly Dictionary<string, int> building = new();
    private readonly SemaphoreSlim maintenance = new(1, 1);

    /// <summary>延长指定缓存代次的活跃保护期限。</summary>
    public void Touch(string key, string generation)
    {
        lock (sync)
        {
            var now = DateTime.UtcNow;
            active[key + "/" + generation] = now;
            try
            {
                Directory.SetLastWriteTimeUtc(Path.Combine(paths.CacheDirectory, "playback", key, generation), now);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>移除失败或已删除缓存代次的活跃记录。</summary>
    public void Forget(string key, string generation)
    {
        lock (sync)
        {
            active.Remove(key + "/" + generation);
        }
    }

    /// <summary>保护同一缓存键下的生成中目录，直到所有构建租约释放。</summary>
    public IDisposable ProtectBuild(string key, string generation)
    {
        lock (sync)
        {
            building.TryGetValue(key, out var count);
            building[key] = count + 1;
            active[key + "/" + generation] = DateTime.UtcNow;
        }
        return new PlaybackCacheBuildLease(() => ReleaseBuild(key));
    }

    /// <summary>检查生成所需磁盘空间；预取不足时跳过，前台尝试回收后报告不足。</summary>
    public async Task<bool> EnsureSpaceAsync(double estimatedBytes, bool prefetch, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var freeBytes = new DriveInfo(paths.CacheDirectory).AvailableFreeSpace;
        if (freeBytes - estimatedBytes > StorageService.MediaThreshold(settings))
        {
            return true;
        }
        if (prefetch)
        {
            return false;
        }
        await MaintainAsync(cancellationToken);
        freeBytes = new DriveInfo(paths.CacheDirectory).AvailableFreeSpace;
        if (freeBytes - estimatedBytes <= StorageService.MediaThreshold(settings))
        {
            throw new InfrastructureException(MessageCode.PLAYBACK_CACHE_SPACE_INSUFFICIENT, 507);
        }
        return true;
    }

    /// <summary>按保留期限和容量清理不活跃的缓存，保留仍在生成或使用的代次。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        await CleanupCoreAsync(null, cancellationToken);
    }

    /// <summary>清理达到最短未使用时间的播放缓存，始终保留生成中与最近五分钟使用的代次。</summary>
    public Task<CacheCleanupCategoryResult> CleanupAsync(int minimumUnusedMinutes, CancellationToken cancellationToken = default)
    {
        return CleanupCoreAsync(minimumUnusedMinutes, cancellationToken);
    }

    private async Task<CacheCleanupCategoryResult> CleanupCoreAsync(int? minimumUnusedMinutes, CancellationToken cancellationToken)
    {
        await maintenance.WaitAsync(cancellationToken);
        try
        {
            var result = new CacheCleanupCategoryResult { Category = CacheCleanupCategories.PLAYBACK };
            var settings = minimumUnusedMinutes.HasValue ? null : await settingsStore.GetAsync(cancellationToken);
            var cacheRoot = paths.CacheDirectory;
            var root = Path.Combine(cacheRoot, "playback");
            var files = new CacheCleanupFileSystem(cacheRoot);
            var keys = files.ReadDirectories(root, result);
            var directories = keys.SelectMany(key => files.ReadDirectories(key.FullName, result))
                .OrderBy(directory => directory.LastWriteTimeUtc).ToArray();
            var snapshots = directories.ToDictionary(directory => directory.FullName,
                directory => files.ReadFiles(directory.FullName, result, cancellationToken, recursive: true));
            var total = snapshots.Values.Sum(snapshot => snapshot.Sum(file => file.Length));
            var deadline = CacheCleanupFileSystem.Cutoff(minimumUnusedMinutes ?? settings!.CacheRetentionMinutes);
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, directory.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var snapshot = snapshots[directory.FullName];
                lock (sync)
                {
                    if (building.ContainsKey(directory.Parent!.Name)
                        || active.TryGetValue(relative, out var access) && DateTime.UtcNow - access < TimeSpan.FromMinutes(5))
                    {
                        result.SkippedEntries += Math.Max(1, snapshot.Count);
                        continue;
                    }
                    var lastUsed = directory.LastWriteTimeUtc;
                    if (snapshot.Count > 0)
                    {
                        lastUsed = new[] { lastUsed, snapshot.Max(file => file.LastWriteTimeUtc), access }.Max();
                    }
                    else if (access > lastUsed)
                    {
                        lastUsed = access;
                    }
                    if (minimumUnusedMinutes.HasValue
                        ? minimumUnusedMinutes.Value > 0 && lastUsed >= deadline
                        : directory.LastWriteTimeUtc >= deadline && total <= settings!.CacheLimitBytes)
                    {
                        result.SkippedEntries += snapshot.Count;
                        continue;
                    }
                    var deletedBefore = result.DeletedBytes;
                    foreach (var file in snapshot)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        files.DeleteFile(file, result);
                    }
                    files.RemoveEmptyTree(directory.FullName, result, cancellationToken);
                    total -= result.DeletedBytes - deletedBefore;
                    if (!Directory.Exists(directory.FullName))
                    {
                        active.Remove(relative);
                    }
                }
            }
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (building.ContainsKey(key.Name))
                    {
                        continue;
                    }
                    if (files.ReadDirectories(key.FullName, result).Count == 0)
                    {
                        foreach (var file in files.ReadFiles(key.FullName, result, cancellationToken))
                        {
                            if (file.Name == "latest.json")
                            {
                                files.DeleteFile(file, result);
                            }
                            else
                            {
                                result.SkippedEntries++;
                            }
                        }
                    }
                    files.RemoveEmptyDirectory(key.FullName, result);
                }
            }
            lock (sync)
            {
                foreach (var key in active.Where(item => DateTime.UtcNow - item.Value > TimeSpan.FromHours(2)).Select(item => item.Key).ToList())
                {
                    active.Remove(key);
                }
            }
            return result;
        }
        finally
        {
            maintenance.Release();
        }
    }

    private void ReleaseBuild(string key)
    {
        lock (sync)
        {
            if (building[key] == 1)
            {
                building.Remove(key);
            }
            else
            {
                building[key]--;
            }
        }
    }

    /// <summary>后台媒体任务结束后释放缓存维护同步资源。</summary>
    public void Dispose()
    {
        maintenance.Dispose();
    }
}
