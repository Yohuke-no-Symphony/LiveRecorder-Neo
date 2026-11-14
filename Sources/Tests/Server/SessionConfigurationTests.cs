using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证共享录像配置的持久化、并发控制和档案生命周期。</summary>
public sealed class SessionConfigurationTests
{
    /// <summary>保存和恢复继承同时更新清单与数据库，拒绝旧版本及非法覆盖。</summary>
    [Fact]
    public async Task OverrideAndInheritancePersistWithOptimisticConcurrency()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        fixture.Settings.Settings.TranslationDanmuPattern = "^全局：";
        var service = Service(fixture);
        Assert.Equal(new(null, "^全局：", 1), await service.GetAsync(session.Id));

        var saved = await service.UpdateAsync(session.Id, new("^录像：", 1));
        Assert.Equal(new("^录像：", "^录像：", 2), saved);
        Assert.Equal("^录像：", (await ReadManifestAsync(fixture, session)).Session.TranslationDanmuPatternOverride);
        Assert.Equal(2, (await ReadSessionAsync(fixture, session.Id)).ConfigurationRevision);
        var stale = await Assert.ThrowsAsync<InfrastructureException>(() => service.UpdateAsync(session.Id, new(null, 1)));
        Assert.Equal(MessageCode.SESSION_CONFIGURATION_CHANGED.ToString(), stale.Code);
        Assert.Equal(409, stale.StatusCode);

        foreach (var invalid in new[] { "", " ", "[", new string('a', 2049) })
        {
            await Assert.ThrowsAsync<InfrastructureException>(() => service.UpdateAsync(session.Id, new(invalid, 2)));
        }
        Assert.Equal(saved, await service.GetAsync(session.Id));
        Assert.Equal(saved, await service.UpdateAsync(session.Id, new("^录像：", 2)));
        Assert.Equal(new(null, "^全局：", 3), await service.UpdateAsync(session.Id, new(null, 2)));
        Assert.Null((await ReadManifestAsync(fixture, session)).Session.TranslationDanmuPatternOverride);
        Assert.Null((await ReadSessionAsync(fixture, session.Id)).TranslationDanmuPatternOverride);
    }

    /// <summary>录制进度旧快照、修复任务以及回收恢复不能覆盖共享配置。</summary>
    [Theory]
    [InlineData("^录像：")]
    [InlineData(null)]
    public async Task LifecyclePreservesTheLastSavedConfiguration(string? pattern)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (stale, segments) = await fixture.SeedSessionAsync();
        var service = Service(fixture);
        await service.UpdateAsync(stale.Id, new("^初始：", 1));
        var saved = await service.UpdateAsync(stale.Id, new(pattern, 2));
        await using (var sink = new RecordingProgressSink(stale, segments, "attempt",
                         Path.Combine(fixture.Root, stale.DirectoryPath), fixture.Mongo, fixture.Publisher, NullLogger.Instance))
        {
            sink.Report(new()
            {
                SegmentId = segments[0].Id,
                RelativePath = segments[0].RelativePath,
                CommittedBytes = segments[0].CommittedBytes,
                DurationMs = segments[0].DurationMs
            });
        }
        Assert.Equal(saved, await service.GetAsync(stale.Id));
        var (_, execution) = await fixture.ExecuteOwnedAsync(JobTestContext.CreateJob(stale, segments, "Repair"));
        await execution;
        Assert.Equal(saved, await service.GetAsync(stale.Id));

        var trash = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await trash.TrashAsync(stale.Id, "member");
        var denied = await Assert.ThrowsAsync<InfrastructureException>(() => service.UpdateAsync(stale.Id, new("^拒绝", saved.Revision)));
        Assert.Equal(404, denied.StatusCode);
        var entry = await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == stale.Id).SingleAsync();
        await trash.RestoreAsync(entry);
        Assert.Equal(saved, await service.GetAsync(stale.Id));
        var restored = await ReadSessionAsync(fixture, stale.Id);
        Assert.Equal(pattern, restored.TranslationDanmuPatternOverride);
        Assert.Equal(saved.Revision, restored.ConfigurationRevision);
    }

    /// <summary>隐藏来源和未发布投影不暴露可编辑配置。</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task UnavailableSessionsRejectConfigurationChanges(bool hidden, bool ready)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.IsHidden, hidden).Set(x => x.ProjectionReady, ready));
        var service = Service(fixture);
        var read = await Assert.ThrowsAsync<InfrastructureException>(() => service.GetAsync(session.Id));
        var update = await Assert.ThrowsAsync<InfrastructureException>(() => service.UpdateAsync(session.Id, new("^录像", 1)));
        Assert.Equal(404, read.StatusCode);
        Assert.Equal(404, update.StatusCode);
    }

    private static SessionConfigurationService Service(JobTestContext fixture) =>
        new(fixture.Mongo, new(fixture.Mongo, TimeProvider.System), fixture.Settings, fixture.Paths);

    private static Task<BroadcastSession> ReadSessionAsync(JobTestContext fixture, string id) =>
        fixture.Mongo.Collection<BroadcastSession>("sessions").Find(x => x.Id == id).SingleAsync();

    private static async Task<SessionManifest> ReadManifestAsync(JobTestContext fixture, BroadcastSession session)
    {
        await using var input = File.OpenRead(Path.Combine(fixture.Root, session.DirectoryPath, "场次.json"));
        return (await JsonSerializer.DeserializeAsync<SessionManifest>(input, JsonFiles.Options))!;
    }
}
