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
            active[key + "/" + generation] = DateTime.UtcNow;
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
        await maintenance.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.GetAsync(cancellationToken);
            var root = Path.Combine(paths.CacheDirectory, "playback");
            if (!Directory.Exists(root))
            {
                return;
            }
            var keys = Directory.EnumerateDirectories(root).ToArray();
            var directories = keys.SelectMany(Directory.EnumerateDirectories)
                .Select(path => new DirectoryInfo(path)).OrderBy(directory => directory.LastWriteTimeUtc).ToList();
            var sizes = directories.ToDictionary(directory => directory.FullName, directory => DirectoryBytes(directory.FullName));
            var total = sizes.Values.Sum();
            var deadline = DateTime.UtcNow.AddMinutes(-settings.CacheRetentionMinutes);
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, directory.FullName).Replace(Path.DirectorySeparatorChar, '/');
                lock (sync)
                {
                    if (building.ContainsKey(directory.Parent!.Name)
                        || active.TryGetValue(relative, out var access) && DateTime.UtcNow - access < TimeSpan.FromMinutes(5))
                    {
                        continue;
                    }
                    if (directory.LastWriteTimeUtc >= deadline && total <= settings.CacheLimitBytes)
                    {
                        continue;
                    }
                    try
                    {
                        Directory.Delete(directory.FullName, recursive: true);
                    }
                    catch (DirectoryNotFoundException)
                    {
                    }
                    total -= sizes[directory.FullName];
                    active.Remove(relative);
                }
            }
            foreach (var directory in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (building.ContainsKey(Path.GetFileName(directory)))
                    {
                        continue;
                    }
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            Directory.Delete(directory);
                        }
                    }
                    catch (DirectoryNotFoundException)
                    {
                    }
                }
            }
            lock (sync)
            {
                foreach (var key in active.Where(item => DateTime.UtcNow - item.Value > TimeSpan.FromHours(2)).Select(item => item.Key).ToList())
                {
                    active.Remove(key);
                }
            }
        }
        finally
        {
            maintenance.Release();
        }
    }

    private static long DirectoryBytes(string path)
    {
        try
        {
            return StorageService.DirectoryBytes(path);
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
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
