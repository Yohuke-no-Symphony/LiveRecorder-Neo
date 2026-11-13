using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>独立读取实际目录占用，复用可取消扫描的短期快照，不参与媒体删除状态机。</summary>
public sealed class StorageUsageService(IAppPaths paths, TimeProvider timeProvider)
{
    private static TimeSpan CacheDuration { get; } = TimeSpan.FromSeconds(30);
    private static StringComparison PathComparison { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private StorageUsageReport? snapshot;

    /// <summary>返回录像和数据两个目录视图；同卷的总容量和空闲容量不能相加。</summary>
    public async Task<IReadOnlyList<StorageVolume>> GetVolumesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        return (await GetDetailsAsync(refresh, cancellationToken)).Volumes;
    }

    /// <summary>读取分类目录快照；强制刷新会合并等待同一次扫描的并发请求。</summary>
    public async Task<StorageUsageReport> GetDetailsAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = Volatile.Read(ref snapshot);
        if (!refresh && IsFresh(observed))
        {
            return observed!;
        }

        await scanGate.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref snapshot);
            if (IsFresh(current) && (!refresh || !ReferenceEquals(current, observed)))
            {
                return current!;
            }

            var records = Path.GetFullPath(paths.RecordsDirectory);
            var data = Path.GetFullPath(paths.DataDirectory);
            var result = await Task.Run(() => Scan(records, data, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref snapshot, result);
            return result;
        }
        finally
        {
            scanGate.Release();
        }
    }

    /// <summary>清除当前缓存，不中断正在扫描的请求。</summary>
    public void Invalidate()
    {
        Volatile.Write(ref snapshot, null);
    }

    private bool IsFresh(StorageUsageReport? value)
    {
        return value is not null
               && timeProvider.GetUtcNow().UtcDateTime - value.ScannedAtUtc < CacheDuration
               && value.Volumes.Count == 2
               && string.Equals(value.Volumes[0].Path, paths.RecordsDirectory, PathComparison)
               && string.Equals(value.Volumes[1].Path, paths.DataDirectory, PathComparison);
    }

    private StorageUsageReport Scan(string records, string data, CancellationToken cancellationToken)
    {
        var state = new StorageUsageScanState();
        var mounts = DriveInfo.GetDrives().Select(drive => Path.TrimEndingDirectorySeparator(drive.Name)).ToArray();
        ScanRoot(records, data, mounts, state, (relative, bytes) => AddRecordingFile(state, records, relative, bytes), cancellationToken);
        if (!string.Equals(records, data, PathComparison))
        {
            ScanRoot(data, records, mounts, state, (relative, bytes) => AddDataFile(state, data, relative, bytes), cancellationToken);
        }
        else
        {
            state.IsPartial = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new()
        {
            ScannedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            IsPartial = state.IsPartial,
            Volumes =
            [
                ReadVolume("recordings", records, state.RecordingBytes, state.ClipBytes, state.TrashBytes, 0),
                ReadVolume("data", data, 0, 0, 0, state.CacheBytes)
            ],
            Rooms = state.Rooms.Values.OrderByDescending(row => row.TotalBytes).ThenBy(row => row.Id).ToArray(),
            Users = state.Users.Values.OrderByDescending(row => row.TotalBytes).ThenBy(row => row.Id).ToArray(),
            Directories = state.Directories.Values.OrderByDescending(row => row.TotalBytes).ThenBy(row => row.Id).ToArray()
        };
    }

    private static void ScanRoot(string root, string overlappingRoot, IReadOnlyList<string> mounts,
        StorageUsageScanState state, Action<string, long> addFile, CancellationToken cancellationToken)
    {
        var excludeOverlap = !string.Equals(root, overlappingRoot, PathComparison) && IsWithin(overlappingRoot, root);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(directory, root, PathComparison)
                && (excludeOverlap && IsWithin(directory, overlappingRoot) || mounts.Any(mount => string.Equals(directory, mount, PathComparison))))
            {
                if (!excludeOverlap || !IsWithin(directory, overlappingRoot))
                {
                    state.IsPartial = true;
                }
                continue;
            }

            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Push(entry);
                        }
                        else
                        {
                            addFile(Path.GetRelativePath(root, entry), new FileInfo(entry).Length);
                        }
                    }
                    catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                    {
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or IOException)
                    {
                        state.IsPartial = true;
                    }
                }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                state.IsPartial = true;
            }
        }
    }

    private static void AddRecordingFile(StorageUsageScanState state, string root, string relative, long bytes)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts[0] == ".trash")
        {
            state.TrashBytes += bytes;
            var directory = parts.Length > 2 ? Path.Combine(parts[0], parts[1]) : parts[0];
            GetDirectory(state, "recordings", root, directory, "trash").TrashBytes += bytes;
            return;
        }
        if (parts[0].StartsWith('.'))
        {
            var directory = parts.Length > 1 ? parts[0] : "";
            GetDirectory(state, "recordings", root, directory, parts[0] == ".work" ? "work" : "other").OtherBytes += bytes;
            return;
        }

        StorageRoomUsage? room = null;
        if (parts.Length > 1)
        {
            if (!state.Rooms.TryGetValue(parts[0], out room))
            {
                room = new()
                {
                    Id = parts[0],
                    Name = parts[0],
                    Path = Path.Combine(root, parts[0])
                };
                state.Rooms.Add(parts[0], room);
            }
        }

        if (parts.Length >= 5 && parts[2] == "切片")
        {
            var owner = parts[3];
            state.ClipBytes += bytes;
            if (room is not null)
            {
                room.ClipBytes += bytes;
            }
            if (!state.Users.TryGetValue(owner, out var user))
            {
                user = new()
                {
                    Id = owner,
                    OwnerId = owner,
                    Name = owner,
                    Path = root
                };
                state.Users.Add(owner, user);
            }
            user.ClipBytes += bytes;
            GetDirectory(state, "recordings", root, Path.Combine(parts[..4]), "clips").ClipBytes += bytes;
            return;
        }

        state.RecordingBytes += bytes;
        if (room is not null)
        {
            room.RecordingBytes += bytes;
        }
        GetDirectory(state, "recordings", root, room is null ? "" : parts[0], "recordings").RecordingBytes += bytes;
    }

    private static void AddDataFile(StorageUsageScanState state, string root, string relative, long bytes)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cache = parts.Length > 1 && parts[0] == "cache";
        if (cache)
        {
            state.CacheBytes += bytes;
        }
        GetDirectory(state, "data", root, parts.Length > 1 ? parts[0] : "", cache ? "cache" : "data").OtherBytes += bytes;
    }

    private static StorageDirectoryUsage GetDirectory(StorageUsageScanState state, string volumeId, string root,
        string relative, string kind)
    {
        var id = volumeId + ":" + (string.IsNullOrEmpty(relative) ? "[" + kind + "]" : relative.Replace(Path.DirectorySeparatorChar, '/'));
        if (!state.Directories.TryGetValue(id, out var directory))
        {
            directory = new()
            {
                Id = id,
                Name = string.IsNullOrEmpty(relative) ? volumeId : relative,
                Path = string.IsNullOrEmpty(relative) ? root : Path.Combine(root, relative),
                Kind = kind
            };
            state.Directories.Add(id, directory);
        }
        return directory;
    }

    private static StorageVolume ReadVolume(string id, string root, long recordingBytes, long clipBytes,
        long trashBytes, long cacheBytes)
    {
        var drive = new DriveInfo(root);
        var totalBytes = drive.TotalSize;
        var freeBytes = drive.AvailableFreeSpace;
        var usedBytes = Math.Max(0, totalBytes - freeBytes - trashBytes);
        return new()
        {
            Id = id,
            Path = root,
            TotalBytes = totalBytes,
            UsedBytes = usedBytes,
            TrashBytes = trashBytes,
            FreeBytes = freeBytes,
            CacheBytes = cacheBytes,
            RecordingBytes = recordingBytes,
            ClipBytes = clipBytes,
            OtherBytes = Math.Max(0, usedBytes - recordingBytes - clipBytes)
        };
    }

    private static bool IsWithin(string path, string root)
    {
        return string.Equals(path, root, PathComparison)
               || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);
    }
}
