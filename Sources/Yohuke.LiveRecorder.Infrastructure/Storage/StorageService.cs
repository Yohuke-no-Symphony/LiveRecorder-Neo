using Yohuke.LiveRecorder.Application.Localization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>通过持久状态与同卷独占锁恢复文件删除，避免使用过期回收站快照操作文件。</summary>
public sealed class StorageService(IMongoContext context, ISettingsStore settingsStore, IAppPaths paths)
{
    private readonly SemaphoreSlim operations = new(1, 1);

    /// <summary>查询录像输出所在卷的余量，不扫描缓存或回收目录。</summary>
    public Task<long> GetFreeBytesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DriveInfo(paths.RecordsDirectory).AvailableFreeSpace);
    }

    /// <summary>派生作业在录制水位之前预留四分之一余量。</summary>
    public static long MediaThreshold(ServerSettings settings)
    {
        var reserve = settings.MinimumFreeBytes / 4;
        return settings.MinimumFreeBytes > long.MaxValue - reserve ? long.MaxValue : settings.MinimumFreeBytes + reserve;
    }

    /// <summary>读取录像主卷容量，只统计录像目录内的回收站，不混入数据目录缓存。</summary>
    public Task<StorageVolume> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = paths.RecordsDirectory;
        return Task.FromResult(ReadVolume("recordings", records, 0, DirectoryBytes(Path.Combine(records, ".trash"))));
    }

    /// <summary>先返回录像主卷，再返回数据目录视图，各自只统计所属回收站或缓存。</summary>
    /// <remarks>两个配置根可能位于同一物理卷，返回容量可重复显示，不能直接汇总 TotalBytes 或 FreeBytes。</remarks>
    public Task<IReadOnlyList<StorageVolume>> GetVolumesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = paths.DataDirectory;
        var records = paths.RecordsDirectory;
        IReadOnlyList<StorageVolume> volumes =
        [
            ReadVolume("recordings", records, 0, DirectoryBytes(Path.Combine(records, ".trash"))),
            ReadVolume("data", data, DirectoryBytes(paths.CacheDirectory), 0)
        ];
        return Task.FromResult(volumes);
    }

    private static StorageVolume ReadVolume(string id, string root, long cacheBytes, long trashBytes)
    {
        var drive = new DriveInfo(root);
        var totalBytes = drive.TotalSize;
        var freeBytes = drive.AvailableFreeSpace;
        return new()
        {
            Id = id,
            Path = root,
            TotalBytes = totalBytes,
            FreeBytes = freeBytes,
            TrashBytes = trashBytes,
            CacheBytes = cacheBytes,
            UsedBytes = Math.Max(0, totalBytes - freeBytes - trashBytes)
        };
    }

    /// <summary>以最新切片记录创建删除意图并将文件移入同卷回收站。</summary>
    public Task TrashClipAsync(Clip clip, CancellationToken cancellationToken = default)
    {
        return TrashClipCoreAsync(clip.Id, false, cancellationToken);
    }

    private async Task TrashClipCoreAsync(string clipId, bool expiredOnly, CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try
        {
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            var settings = await settingsStore.GetAsync(cancellationToken);
            await using var fileLock = AcquireFileLock(paths.RecordsDirectory, clipId);
            await context.Collection<TrashEntry>("trash").Indexes.CreateOneAsync(new CreateIndexModel<TrashEntry>(
                Builders<TrashEntry>.IndexKeys.Ascending(x => x.EntityId), new CreateIndexOptions { Name = "trash_entity_unique", Unique = true }), cancellationToken: cancellationToken);
            var existing = await context.Collection<TrashEntry>("trash").Find(x => x.EntityId == clipId).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (existing.State is "Pending" or "Moving")
                {
                    await CompleteMoveAsync(existing, cancellationToken);
                }
                return;
            }
            var clip = await context.Collection<Clip>("clips").Find(x => x.Id == clipId).FirstOrDefaultAsync(cancellationToken);
            if (clip is null || clip.IsTrashed || expiredOnly && clip.ExpiresAtUtc > DateTime.UtcNow)
            {
                return;
            }
            var entry = new TrashEntry
            {
                OwnerId = clip.OwnerId,
                EntityId = clip.Id,
                Title = clip.Title,
                OriginalRelativePath = clip.RelativePath,
                Bytes = clip.Bytes
            };
            entry.PurgeAtUtc = TrashRetentionPolicy.GetPurgeAtUtc(entry, settings);
            entry.TrashRelativePath = Path.Combine(".trash", entry.Id, Path.GetFileName(clip.RelativePath));
            await context.Collection<TrashEntry>("trash").InsertOneAsync(entry, cancellationToken: cancellationToken);
            await CompleteMoveAsync(entry, cancellationToken);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>只恢复仍存在的当前删除意图，不覆盖原位置已有文件。</summary>
    public async Task RestoreAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        await OperateEntryAsync(entry.Id, async current =>
        {
            if (current.State == "Purging")
            {
                throw new InfrastructureException(MessageCode.STORAGE_PURGING, 409);
            }
            if (current.State is "Pending" or "Moving")
            {
                current = await CompleteMoveAsync(current, cancellationToken);
            }
            await CompleteRestoreAsync(current, await settingsStore.GetAsync(cancellationToken), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>只删除当前回收路径；已恢复或正在恢复的条目不会再次移动原文件。</summary>
    public Task PurgeAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        return PurgeCoreAsync(entry, false, cancellationToken);
    }

    private async Task PurgeCoreAsync(TrashEntry entry, bool expiredOnly, CancellationToken cancellationToken)
    {
        await OperateEntryAsync(entry.Id, async current =>
        {
            if (expiredOnly && current.State != "Purging")
            {
                var purgeAt = TrashRetentionPolicy.GetPurgeAtUtc(current,
                    await settingsStore.GetAsync(cancellationToken));
                if (current.PurgeAtUtc != purgeAt)
                {
                    await context.Collection<TrashEntry>("trash").UpdateOneAsync(x => x.Id == current.Id,
                        Builders<TrashEntry>.Update.Set(x => x.PurgeAtUtc, purgeAt),
                        cancellationToken: cancellationToken);
                    current.PurgeAtUtc = purgeAt;
                }

                if (purgeAt is null || purgeAt > DateTime.UtcNow)
                {
                    return;
                }
            }

            if (current.State == "Restoring")
            {
                await CompleteRestoreAsync(current, await settingsStore.GetAsync(cancellationToken), cancellationToken);
                return;
            }
            if (current.State is "Pending" or "Moving")
            {
                current = await CompleteMoveAsync(current, cancellationToken);
            }
            current = await ClaimAsync(current, "Purging", cancellationToken);
            await RequireOperationAsync(current, cancellationToken);
            var path = MediaPaths.Resolve(paths.RecordsDirectory, current.TrashRelativePath);
            File.Delete(path);
            await context.Collection<Clip>("clips").DeleteOneAsync(x => x.Id == current.EntityId && x.IsTrashed && x.RelativePath == current.OriginalRelativePath, cancellationToken);
            await context.Collection<TrashEntry>("trash").DeleteOneAsync(OperationFilter(current), cancellationToken);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }, cancellationToken);
    }

    /// <summary>恢复未完成操作，并基于最新记录处理到期切片及回收条目。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        var pending = await context.Collection<TrashEntry>("trash").Find(x => x.Kind == "Clip" && x.State != "Active").ToListAsync(cancellationToken);
        foreach (var entry in pending)
        {
            try
            {
                if (entry.State == "Restoring")
                {
                    await RestoreAsync(entry, cancellationToken);
                }
                else if (entry.State == "Purging")
                {
                    await PurgeAsync(entry, cancellationToken);
                }
                else
                {
                    await OperateEntryAsync(entry.Id, async current =>
                    {
                        if (current.State is "Pending" or "Moving")
                        {
                            await CompleteMoveAsync(current, cancellationToken);
                        }
                    }, cancellationToken);
                }
            }
            catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
            {
            }
        }
        var expired = await context.Collection<Clip>("clips").Find(x => !x.IsTrashed && x.ExpiresAtUtc <= DateTime.UtcNow).ToListAsync(cancellationToken);
        foreach (var clip in expired)
        {
            try
            {
                await TrashClipCoreAsync(clip.Id, true, cancellationToken);
            }
            catch (InfrastructureException error) when (error.StatusCode == 409)
            {
            }
        }
        var purge = await context.Collection<TrashEntry>("trash").Find(x => x.Kind == "Clip")
            .ToListAsync(cancellationToken);
        foreach (var entry in purge)
        {
            try
            {
                await PurgeCoreAsync(entry, true, cancellationToken);
            }
            catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
            {
            }
        }
    }

    private async Task OperateEntryAsync(string id, Func<TrashEntry, Task> operation, CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try
        {
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            var initial = await context.Collection<TrashEntry>("trash").Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (initial is null)
            {
                return;
            }
            await using var fileLock = AcquireFileLock(paths.RecordsDirectory, initial.EntityId);
            var current = await context.Collection<TrashEntry>("trash").Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (current is not null)
            {
                await operation(current);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    private async Task<TrashEntry> CompleteMoveAsync(TrashEntry entry, CancellationToken cancellationToken)
    {
        if (entry.State is not ("Pending" or "Moving"))
        {
            return entry;
        }
        entry = await ClaimAsync(entry, "Moving", cancellationToken);
        var clip = await context.Collection<Clip>("clips").Find(x => x.Id == entry.EntityId && x.RelativePath == entry.OriginalRelativePath).FirstOrDefaultAsync(cancellationToken);
        if (clip is null)
        {
            await context.Collection<TrashEntry>("trash").DeleteOneAsync(OperationFilter(entry), cancellationToken);
            throw new InfrastructureException(MessageCode.STORAGE_SOURCE_CLIP_CHANGED, 410);
        }
        var source = MediaPaths.Resolve(paths.RecordsDirectory, entry.OriginalRelativePath);
        var destination = MediaPaths.Resolve(paths.RecordsDirectory, entry.TrashRelativePath);
        if (File.Exists(source) && File.Exists(destination))
        {
            throw new InfrastructureException(MessageCode.STORAGE_TRASH_PATH_CONFLICT, 409);
        }
        await RequireOperationAsync(entry, cancellationToken);
        await context.Collection<Clip>("clips").UpdateOneAsync(x => x.Id == entry.EntityId && x.RelativePath == entry.OriginalRelativePath,
            Builders<Clip>.Update.Set(x => x.IsTrashed, true), cancellationToken: cancellationToken);
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, false);
        }
        var changed = await context.Collection<TrashEntry>("trash").UpdateOneAsync(OperationFilter(entry), Builders<TrashEntry>.Update
            .Set(x => x.State, "Active").Set(x => x.OperationOwner, null).Set(x => x.OperationLeaseUntilUtc, null), cancellationToken: cancellationToken);
        if (changed.MatchedCount == 0)
        {
            throw new InfrastructureException(MessageCode.STORAGE_TRASH_LEASE_LOST, 409);
        }
        entry.State = "Active";
        entry.OperationOwner = null;
        entry.OperationLeaseUntilUtc = null;
        return entry;
    }

    private async Task CompleteRestoreAsync(TrashEntry entry, ServerSettings settings, CancellationToken cancellationToken)
    {
        if (entry.State is not ("Active" or "Restoring"))
        {
            throw new InfrastructureException(MessageCode.STORAGE_RESTORE_STATE_INVALID, 409);
        }
        var resuming = entry.State == "Restoring";
        var source = MediaPaths.Resolve(paths.RecordsDirectory, entry.TrashRelativePath);
        var destination = MediaPaths.Resolve(paths.RecordsDirectory, entry.OriginalRelativePath);
        if (File.Exists(destination) && (!resuming || File.Exists(source)))
        {
            throw new InfrastructureException(MessageCode.STORAGE_RESTORE_DESTINATION_EXISTS, 409);
        }
        if (!File.Exists(source) && !File.Exists(destination))
        {
            throw new InfrastructureException(MessageCode.STORAGE_TRASH_FILE_MISSING, 410);
        }
        entry = await ClaimAsync(entry, "Restoring", cancellationToken);
        await RequireOperationAsync(entry, cancellationToken);
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, false);
        }
        await context.Collection<Clip>("clips").UpdateOneAsync(x => x.Id == entry.EntityId && x.RelativePath == entry.OriginalRelativePath,
            Builders<Clip>.Update.Set(x => x.IsTrashed, false).Set(x => x.ExpiresAtUtc, DateTime.UtcNow.AddDays(settings.ClipRetentionDays)), cancellationToken: cancellationToken);
        await context.Collection<TrashEntry>("trash").DeleteOneAsync(OperationFilter(entry), cancellationToken);
    }

    private async Task<TrashEntry> ClaimAsync(TrashEntry entry, string state, CancellationToken cancellationToken)
    {
        var changed = await context.Collection<TrashEntry>("trash").FindOneAndUpdateAsync(x => x.Id == entry.Id && x.State == entry.State && x.OperationOwner == entry.OperationOwner,
            Builders<TrashEntry>.Update.Set(x => x.State, state).Set(x => x.OperationOwner, Guid.NewGuid().ToString("N")).Set(x => x.OperationLeaseUntilUtc, DateTime.UtcNow.AddMinutes(2)),
            new FindOneAndUpdateOptions<TrashEntry> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        return changed ?? throw new InfrastructureException(MessageCode.STORAGE_OPERATION_TAKEN_OVER, 409);
    }

    private async Task RequireOperationAsync(TrashEntry entry, CancellationToken cancellationToken)
    {
        if (!await context.Collection<TrashEntry>("trash").Find(OperationFilter(entry)).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.STORAGE_LEASE_EXPIRED, 409);
        }
    }

    private static FilterDefinition<TrashEntry> OperationFilter(TrashEntry entry)
    {
        return Builders<TrashEntry>.Filter.Where(x => x.Id == entry.Id && x.State == entry.State && x.OperationOwner == entry.OperationOwner)
               & new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$OperationLeaseUntilUtc", "$$NOW" }));
    }

    private static FileStream AcquireFileLock(string root, string entityId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entityId)));
        var path = MediaPaths.Resolve(root, Path.Combine(".trash", ".locks", key + ".lock"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InfrastructureException(MessageCode.STORAGE_OPERATION_IN_PROGRESS, 409);
        }
    }

    /// <summary>统计目录字节，跳过链接并容忍并发清理已移走的文件。</summary>
    public static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }
        var total = 0L;
        foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        return total;
    }
}
