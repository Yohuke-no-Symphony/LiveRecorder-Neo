using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>以独立 MongoDB 和真实文件验证回收站的并发恢复边界。</summary>
public sealed class StorageReliabilityTests
{
    /// <summary>过期的恢复及彻底删除请求不能再次移动已经恢复的文件。</summary>
    [Fact]
    public async Task StaleTrashEntryCannotDeleteOrMoveRestoredClip()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var clip = await SeedClipAsync(fixture);
        await fixture.Storage.TrashClipAsync(clip);
        var stale = await fixture.Mongo.Collection<TrashEntry>("trash").Find(entry => entry.EntityId == clip.Id).SingleAsync();
        await fixture.Storage.RestoreAsync(stale);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new StorageService(fixture.Mongo, fixture.Settings, fixture.Paths).PurgeAsync(stale)));
        await fixture.Storage.RestoreAsync(stale);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(fixture.Root, clip.RelativePath)));
        var current = await fixture.Mongo.Collection<Clip>("clips").Find(item => item.Id == clip.Id).SingleAsync();
        Assert.False(current.IsTrashed);
        Assert.True(current.ExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal(0, await fixture.Mongo.Collection<TrashEntry>("trash").CountDocumentsAsync(_ => true));
    }

    /// <summary>恢复已移动文件但尚未提交数据库时，过期的 purge 只能完成恢复。</summary>
    [Fact]
    public async Task PurgeResumesRestoringEntryWithoutReTrashingFile()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var clip = await SeedClipAsync(fixture);
        await fixture.Storage.TrashClipAsync(clip);
        var stale = await fixture.Mongo.Collection<TrashEntry>("trash").Find(entry => entry.EntityId == clip.Id).SingleAsync();
        await fixture.Mongo.Collection<TrashEntry>("trash").UpdateOneAsync(entry => entry.Id == stale.Id, Builders<TrashEntry>.Update
            .Set(entry => entry.State, "Restoring").Set(entry => entry.OperationOwner, "crashed").Set(entry => entry.OperationLeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        File.Move(Path.Combine(fixture.Root, stale.TrashRelativePath), Path.Combine(fixture.Root, stale.OriginalRelativePath));
        await new StorageService(fixture.Mongo, fixture.Settings, fixture.Paths).PurgeAsync(stale);
        Assert.True(File.Exists(Path.Combine(fixture.Root, clip.RelativePath)));
        Assert.False((await fixture.Mongo.Collection<Clip>("clips").Find(item => item.Id == clip.Id).SingleAsync()).IsTrashed);
        Assert.Equal(0, await fixture.Mongo.Collection<TrashEntry>("trash").CountDocumentsAsync(_ => true));
    }

    /// <summary>恢复冲突时同时保留原位置的新文件与回收文件。</summary>
    [Fact]
    public async Task RestoreNeverOverwritesExistingDestination()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var clip = await SeedClipAsync(fixture);
        await fixture.Storage.TrashClipAsync(clip);
        var entry = await fixture.Mongo.Collection<TrashEntry>("trash").Find(item => item.EntityId == clip.Id).SingleAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, clip.RelativePath), "another-file");
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Storage.RestoreAsync(entry));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("another-file", await File.ReadAllTextAsync(Path.Combine(fixture.Root, clip.RelativePath)));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(fixture.Root, entry.TrashRelativePath)));
    }

    /// <summary>创建回收条目时即保证实体唯一索引，不依赖后台索引任务。</summary>
    [Fact]
    public async Task ConcurrentTrashCreatesOnePersistentEntry()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var clip = await SeedClipAsync(fixture);
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await new StorageService(fixture.Mongo, fixture.Settings, fixture.Paths).TrashClipAsync(clip);
            }
            catch (InfrastructureException error) when (error.StatusCode == 409)
            {
            }
        });
        await Task.WhenAll(tasks);
        Assert.Equal(1, await fixture.Mongo.Collection<TrashEntry>("trash").CountDocumentsAsync(entry => entry.EntityId == clip.Id));
        var entries = await (await fixture.Mongo.Collection<TrashEntry>("trash").Indexes.ListAsync()).ToListAsync();
        Assert.Contains(entries, entry => entry["name"] == "trash_entity_unique" && entry["unique"].AsBoolean);
    }

    private static async Task<Clip> SeedClipAsync(JobTestContext fixture)
    {
        var clip = new Clip { RelativePath = "clips/fixture.mp4", OwnerId = "owner", ExpiresAtUtc = DateTime.UtcNow.AddHours(1), Bytes = 8 };
        Directory.CreateDirectory(Path.Combine(fixture.Root, "clips"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, clip.RelativePath), "original");
        await fixture.Mongo.Collection<Clip>("clips").InsertOneAsync(clip);
        return clip;
    }
}
