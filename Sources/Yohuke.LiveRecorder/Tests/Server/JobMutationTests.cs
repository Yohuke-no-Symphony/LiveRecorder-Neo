using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证修复互斥索引与并发状态 API 不依赖过期客户端快照。</summary>
public sealed class JobMutationTests
{
    /// <summary>初次使用时就创建唯一互斥索引，并发修复只允许一个排队。</summary>
    [Fact]
    public async Task ConcurrentRepairCreationUsesUniqueMutationKeyImmediately()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var service = new MediaJobService(fixture.Mongo, new LibraryService(fixture.Mongo, TimeProvider.System), fixture.Publisher);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                return await service.CreateAsync(new CreateMediaJobRequest { SessionId = session.Id, Kind = "Repair" }, "admin", true);
            }
            catch (InfrastructureException error) when (error.StatusCode == 409)
            {
                return null;
            }
        }));
        Assert.Single(results, job => job is not null);
        Assert.Equal(1, await fixture.Mongo.Collection<MediaJob>("jobs").CountDocumentsAsync(job => job.MutationKey == session.Id));
    }

    /// <summary>重试重新取得互斥键，有其他修复任务时拒绝并保持原终态。</summary>
    [Fact]
    public async Task RetryCannotBypassRepairMutationLock()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var service = new MediaJobService(fixture.Mongo, new LibraryService(fixture.Mongo, TimeProvider.System), fixture.Publisher);
        var failed = await service.CreateAsync(new CreateMediaJobRequest { SessionId = session.Id, Kind = "Repair" }, "admin", true);
        await fixture.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(job => job.Id == failed.Id, Builders<MediaJob>.Update.Set(job => job.State, "Failed").Set(job => job.MutationKey, null));
        var active = await service.CreateAsync(new CreateMediaJobRequest { SessionId = session.Id, Kind = "Repair" }, "admin", true);
        Assert.Equal(409, (await Assert.ThrowsAsync<InfrastructureException>(() => service.RetryAsync(failed))).StatusCode);
        await service.CancelAsync(active);
        await service.RetryAsync(failed);
        var current = await fixture.Mongo.Collection<MediaJob>("jobs").Find(job => job.Id == failed.Id).SingleAsync();
        Assert.Equal("Queued", current.State);
        Assert.Equal(session.Id, current.MutationKey);
    }

    /// <summary>旧的排队快照不能通过取消请求修改已经完成的数据库状态。</summary>
    [Fact]
    public async Task StaleCancelCannotModifyCompletedJob()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var job = new MediaJob { State = "Completed", Progress = 100 };
        await fixture.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var service = new MediaJobService(fixture.Mongo, new LibraryService(fixture.Mongo, TimeProvider.System), fixture.Publisher);
        await service.CancelAsync(new MediaJob { Id = job.Id, State = "Queued" });
        var current = await fixture.Mongo.Collection<MediaJob>("jobs").Find(item => item.Id == job.Id).SingleAsync();
        Assert.Equal("Completed", current.State);
        Assert.False(current.CancelRequested);
        Assert.Equal(100, current.Progress);
    }
}
