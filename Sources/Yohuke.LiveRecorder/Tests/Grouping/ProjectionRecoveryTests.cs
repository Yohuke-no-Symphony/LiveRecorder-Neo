using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Grouping.Tests;

/// <summary>验证拆分批量日志在部分发布及重启时的完整可见性。</summary>
public sealed class ProjectionRecoveryTests
{
    /// <summary>旧归组日志不得回退已经发布后发生的媒体修复版本。</summary>
    [Fact]
    public async Task ReplayCannotRollbackLaterMediaRevision()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var left = GroupingFixture.Project(source, false, 1000);
        var right = GroupingFixture.Project(source, true, 1000);
        var journal = Path.Combine(fixture.Root, ".work", "groups", "old-completed", "operation.json");
        await JsonFiles.WriteAsync(journal, new ProjectionOperation { Outputs = [left, right], SourceIds = [source.Session.Id] });
        left.Session.ProjectionReady = true;
        left.Session.MediaRevision = 2;
        left.Session.MediaRevisionJobId = "later-repair";
        left.Session.MediaRevisionAttempt = 1;
        right.Session.ProjectionReady = true;
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertManyAsync([left.Session, right.Session]);
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(left.Segments.Concat(right.Segments));
        var repaired = new RecordingSegment
        {
            Id = "later-repaired", SessionId = left.Session.Id, Revision = 2, IsClosed = true,
            RelativePath = left.Segments[0].RelativePath, RootRelativePath = left.Segments[0].RootRelativePath,
            RootIndexRelativePath = left.Segments[0].RootIndexRelativePath, DurationMs = 1000, CommittedBytes = left.Segments[0].CommittedBytes
        };
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertOneAsync(repaired);
        await fixture.Service().ReconcileAsync();
        var current = await fixture.Library.GetAsync(left.Session.Id);
        Assert.Equal(2, current.Session.MediaRevision);
        Assert.Equal("later-repair", current.Session.MediaRevisionJobId);
        Assert.Equal("later-repaired", Assert.Single(current.Segments).Id);
    }

    /// <summary>只落盘上半场时仍保留完整原场次，重启后发布两侧再隐藏源。</summary>
    [Fact]
    public async Task InterruptedSplitJournalRecoversBothOutputsBeforeHidingSource()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var left = GroupingFixture.Project(source, false, 1000);
        var right = GroupingFixture.Project(source, true, 1000);
        var operation = new ProjectionOperation { Outputs = [left, right], SourceIds = [source.Session.Id] };
        var journal = Path.Combine(fixture.Root, ".work", "groups", "interrupted", "operation.json");
        await JsonFiles.WriteAsync(journal, operation);
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, left.Session.DirectoryPath, "场次.json"), left);
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(left.Segments);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(left.Session);
        Assert.False((await fixture.Library.GetAsync(source.Session.Id)).Session.IsHidden);
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Library.GetAsync(left.Session.Id));
        await fixture.Service().ReconcileAsync();
        Assert.True((await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(session => session.Id == source.Session.Id).SingleAsync()).IsHidden);
        Assert.True((await fixture.Library.GetAsync(left.Session.Id)).Session.ProjectionReady);
        Assert.True((await fixture.Library.GetAsync(right.Session.Id)).Session.ProjectionReady);
        Assert.False(File.Exists(journal));
        await fixture.Service().ReconcileAsync();
        Assert.Equal(3, await fixture.Mongo.Collection<BroadcastSession>("sessions").CountDocumentsAsync(_ => true));
    }

    /// <summary>激活第一侧后崩溃时，源仍可见，重启补齐第二侧。</summary>
    [Fact]
    public async Task InterruptedActivationRetainsCompleteSourceUntilBothSidesReady()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var left = GroupingFixture.Project(source, false, 1000);
        var right = GroupingFixture.Project(source, true, 1000);
        var journal = Path.Combine(fixture.Root, ".work", "groups", "activation", "operation.json");
        await JsonFiles.WriteAsync(journal, new ProjectionOperation { Outputs = [left, right], SourceIds = [source.Session.Id] });
        left.Session.ProjectionReady = true;
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertManyAsync([left.Session, right.Session]);
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(left.Segments.Concat(right.Segments));
        Assert.False((await fixture.Library.GetAsync(source.Session.Id)).Session.IsHidden);
        await fixture.Service().ReconcileAsync();
        Assert.True((await fixture.Library.GetAsync(left.Session.Id)).Session.ProjectionReady);
        Assert.True((await fixture.Library.GetAsync(right.Session.Id)).Session.ProjectionReady);
        Assert.True((await fixture.Library.GetAsync(source.Session.Id)).Session.IsHidden);
    }

    /// <summary>已全部发布后的日志重放不能将 ready 降级，否则重放中断会只剩一个半场。</summary>
    [Fact]
    public async Task ReplayCannotDeactivateAlreadyPublishedProjection()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var left = GroupingFixture.Project(source, false, 1000);
        var right = GroupingFixture.Project(source, true, 1000);
        var journal = Path.Combine(fixture.Root, ".work", "groups", "replay", "operation.json");
        await JsonFiles.WriteAsync(journal, new ProjectionOperation { Outputs = [left, right], SourceIds = [source.Session.Id] });
        left.Session.ProjectionReady = true;
        right.Session.ProjectionReady = true;
        left.Session.IsHidden = true;
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertManyAsync([left.Session, right.Session]);
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertManyAsync(left.Segments.Concat(right.Segments));
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(session => session.Id == source.Session.Id, Builders<BroadcastSession>.Update.Set(session => session.IsHidden, true));
        Directory.CreateDirectory(Path.Combine(fixture.Root, right.Session.DirectoryPath, "场次.json"));
        try
        {
            await fixture.Service().ReconcileAsync();
        }
        catch (IOException)
        {
        }
        var published = await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(session => session.Id == left.Session.Id || session.Id == right.Session.Id).ToListAsync();
        Assert.All(published, session => Assert.True(session.ProjectionReady));
        Assert.True(published.Single(session => session.Id == left.Session.Id).IsHidden);
        Assert.True((await fixture.Library.GetAsync(source.Session.Id)).Session.IsHidden);
    }
}
