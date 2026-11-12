using Yohuke.LiveRecorder.Application.Localization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>通过持久状态与同卷独占锁恢复文件删除，避免使用过期回收站快照操作文件。</summary>
public sealed class StorageService(MongoContext context, ISettingsStore settingsStore)
{
    private readonly SemaphoreSlim operations = new(1, 1);

    /// <summary>只查询文件系统余量，不扫描缓存或回收目录。</summary>
    public async Task<long> GetFreeBytesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        return new DriveInfo(Path.GetFullPath(settings.StoragePath)).AvailableFreeSpace;
    }

    /// <summary>派生作业在录制水位之前预留四分之一余量。</summary>
    public static long MediaThreshold(ServerSettings settings)
    {
        var reserve = settings.MinimumFreeBytes / 4;
        return settings.MinimumFreeBytes > long.MaxValue - reserve ? long.MaxValue : settings.MinimumFreeBytes + reserve;
    }

    /// <summary>读取实际存储卷容量，其他文件占用也纳入已用空间。</summary>
    public async Task<StorageVolume> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var root = Path.GetFullPath(settings.StoragePath);
        var drive = new DriveInfo(root);
        var trashBytes = DirectoryBytes(Path.Combine(root, ".trash"));
        return new()
        {
            Path = root,
            TotalBytes = drive.TotalSize,
            FreeBytes = drive.AvailableFreeSpace,
            TrashBytes = trashBytes,
            CacheBytes = DirectoryBytes(Path.Combine(root, ".cache")),
            UsedBytes = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace - trashBytes)
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
            var settings = await settingsStore.GetAsync(cancellationToken);
            await using var fileLock = AcquireFileLock(settings.StoragePath, clipId);
            await context.Collection<TrashEntry>("trash").Indexes.CreateOneAsync(new CreateIndexModel<TrashEntry>(
                Builders<TrashEntry>.IndexKeys.Ascending(x => x.EntityId), new CreateIndexOptions { Name = "trash_entity_unique", Unique = true }), cancellationToken: cancellationToken);
            var existing = await context.Collection<TrashEntry>("trash").Find(x => x.EntityId == clipId).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (existing.State is "Pending" or "Moving")
                {
                    await CompleteMoveAsync(existing, settings, cancellationToken);
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
                Bytes = clip.Bytes,
                PurgeAtUtc = DateTime.UtcNow.AddDays(settings.TrashRetentionDays)
            };
            entry.TrashRelativePath = Path.Combine(".trash", entry.Id, Path.GetFileName(clip.RelativePath));
            await context.Collection<TrashEntry>("trash").InsertOneAsync(entry, cancellationToken: cancellationToken);
            await CompleteMoveAsync(entry, settings, cancellationToken);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>只恢复仍存在的当前删除意图，不覆盖原位置已有文件。</summary>
    public async Task RestoreAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        await OperateEntryAsync(entry.Id, async (current, settings) =>
        {
            if (current.State == "Purging")
            {
                throw new InfrastructureException(MessageCode.STORAGE_PURGING, 409);
            }
            if (current.State is "Pending" or "Moving")
            {
                current = await CompleteMoveAsync(current, settings, cancellationToken);
            }
            await CompleteRestoreAsync(current, settings, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>只删除当前回收路径；已恢复或正在恢复的条目不会再次移动原文件。</summary>
    public async Task PurgeAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        await OperateEntryAsync(entry.Id, async (current, settings) =>
        {
            if (current.State == "Restoring")
            {
                await CompleteRestoreAsync(current, settings, cancellationToken);
                return;
            }
            if (current.State is "Pending" or "Moving")
            {
                current = await CompleteMoveAsync(current, settings, cancellationToken);
            }
            current = await ClaimAsync(current, "Purging", cancellationToken);
            await RequireOperationAsync(current, cancellationToken);
            var path = MediaPaths.Resolve(settings.StoragePath, current.TrashRelativePath);
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
        var pending = await context.Collection<TrashEntry>("trash").Find(x => x.State != "Active").ToListAsync(cancellationToken);
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
                    await OperateEntryAsync(entry.Id, async (current, settings) =>
                    {
                        if (current.State is "Pending" or "Moving")
                        {
                            await CompleteMoveAsync(current, settings, cancellationToken);
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
        var purge = await context.Collection<TrashEntry>("trash").Find(x => x.PurgeAtUtc <= DateTime.UtcNow).ToListAsync(cancellationToken);
        foreach (var entry in purge)
        {
            try
            {
                await PurgeAsync(entry, cancellationToken);
            }
            catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
            {
            }
        }
    }

    private async Task OperateEntryAsync(string id, Func<TrashEntry, ServerSettings, Task> operation, CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try
        {
            var initial = await context.Collection<TrashEntry>("trash").Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (initial is null)
            {
                return;
            }
            var settings = await settingsStore.GetAsync(cancellationToken);
            await using var fileLock = AcquireFileLock(settings.StoragePath, initial.EntityId);
            var current = await context.Collection<TrashEntry>("trash").Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (current is not null)
            {
                await operation(current, settings);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    private async Task<TrashEntry> CompleteMoveAsync(TrashEntry entry, ServerSettings settings, CancellationToken cancellationToken)
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
        var source = MediaPaths.Resolve(settings.StoragePath, entry.OriginalRelativePath);
        var destination = MediaPaths.Resolve(settings.StoragePath, entry.TrashRelativePath);
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
        var source = MediaPaths.Resolve(settings.StoragePath, entry.TrashRelativePath);
        var destination = MediaPaths.Resolve(settings.StoragePath, entry.OriginalRelativePath);
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
