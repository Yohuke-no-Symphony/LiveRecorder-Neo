using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Grouping.Tests;

/// <summary>以真实管理数据库与不可变源文件验证手动归组及物理拆分。</summary>
public sealed class SessionGroupingTests
{
    /// <summary>旧录播没有索引时保持空引用，不把场次目录误当成索引文件。</summary>
    [Fact]
    public async Task GroupAndSplitPreserveNullSourceIndex()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(10000);
        await fixture.Mongo.Collection<RecordingSegment>("segments").UpdateManyAsync(
            segment => segment.SessionId == first.Session.Id || segment.SessionId == second.Session.Id,
            Builders<RecordingSegment>.Update.Set(segment => segment.IndexRelativePath, "").Set(segment => segment.RootIndexRelativePath, null));
        var grouped = await fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], null);
        var detail = await fixture.Library.GetAsync(grouped.Id);
        Assert.All(detail.Segments, segment =>
        {
            Assert.Null(segment.RootIndexRelativePath);
            Assert.Equal("", MediaPaths.ResolveSegmentIndex(fixture.Root, grouped.DirectoryPath, segment));
            Assert.True(File.Exists(MediaPaths.ResolveSegment(fixture.Root, grouped.DirectoryPath, segment)));
        });
        var split = await fixture.Service().SplitAsync(grouped.Id, detail.Segments.First(segment => segment.StartOffsetMs >= 10000).Id);
        foreach (var session in split)
        {
            Assert.All((await fixture.Library.GetAsync(session.Id)).Segments, segment =>
            {
                Assert.Null(segment.RootIndexRelativePath);
                Assert.Equal("", MediaPaths.ResolveSegmentIndex(fixture.Root, session.DirectoryPath, segment));
            });
        }
    }

    /// <summary>归组保留 UTC 空档、主播信息及原始文件引用，不复制媒体。</summary>
    [Fact]
    public async Task GroupKeepsUtcGapAndAllOriginalArchiveBytes()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(10000);
        await fixture.WriteEventsAsync(first.Session, 0, 400, 1600);
        await fixture.WriteEventsAsync(second.Session, 100, 1800);
        var before = fixture.ArchiveHashes();
        var grouped = await fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], "combined");
        var detail = await fixture.Library.GetAsync(grouped.Id);
        Assert.Equal(GroupingFixture.Origin, grouped.StartedAtUtc);
        Assert.Equal(GroupingFixture.Origin.AddMilliseconds(12000), grouped.EndedAtUtc);
        Assert.Equal("streamer", grouped.StreamerId);
        Assert.Equal(12000, detail.DurationMs);
        Assert.Contains(detail.Gaps, gap => gap.StartMs == 2000 && gap.EndMs == 10000);
        Assert.Equal(4, detail.Segments.Count);
        Assert.All(detail.Segments, segment =>
        {
            Assert.True(File.Exists(MediaPaths.ResolveSegment(fixture.Root, grouped.DirectoryPath, segment)));
            Assert.True(File.Exists(MediaPaths.ResolveSegmentIndex(fixture.Root, grouped.DirectoryPath, segment)));
        });
        Assert.Equal(before.OrderBy(pair => pair.Key), fixture.ArchiveHashes().OrderBy(pair => pair.Key));
        var sources = await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(session => session.Id == first.Session.Id || session.Id == second.Session.Id).ToListAsync();
        Assert.All(sources, source => Assert.True(source.IsHidden));
        Assert.True(grouped.ProjectionReady);
    }

    /// <summary>其他主播或重叠场次不能归组。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupRejectsDifferentStreamerOrOverlap(bool overlap)
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(overlap ? 1000 : 10000, streamerId: overlap ? "streamer" : "another-streamer");
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], null));
        Assert.False((await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(session => session.Id == first.Session.Id).SingleAsync()).IsHidden);
    }

    /// <summary>即使只有五十毫秒也属于重叠，不能把同一 UTC 时段重复归组。</summary>
    [Fact]
    public async Task GroupRejectsFiftyMillisecondOverlap()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(1950);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], null));
        Assert.Equal(422, error.StatusCode);
    }

    /// <summary>物理拆分两侧通过根引用保留原文件。</summary>
    [Fact]
    public async Task SplitUsesNonOverlappingPhysicalBoundaryWithoutTouchingSources()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var before = fixture.ArchiveHashes();
        var split = await fixture.Service().SplitAsync(source.Session.Id, source.Segments[1].Id);
        Assert.Equal(2, split.Count);
        var left = await fixture.Library.GetAsync(split[0].Id);
        var right = await fixture.Library.GetAsync(split[1].Id);
        Assert.Equal(1000, left.DurationMs);
        Assert.Equal(1000, right.DurationMs);
        Assert.Equal(0, Assert.Single(right.Segments).StartOffsetMs);
        Assert.Equal(split[0].EndedAtUtc, split[1].StartedAtUtc);
        Assert.Equal(before.OrderBy(pair => pair.Key), fixture.ArchiveHashes().OrderBy(pair => pair.Key));
    }

    /// <summary>任意重叠物理片段都不能充当拆分点，包括小于百毫秒的重叠。</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    public async Task SplitRejectsOverlappingBoundary(double overlap)
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000 + overlap), (1000, 1000)]);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().SplitAsync(source.Session.Id, source.Segments[1].Id));
        Assert.Equal(422, error.StatusCode);
    }

    /// <summary>首片段和不存在的片段不能生成空的上半场。</summary>
    [Fact]
    public async Task SplitRejectsFirstAndUnknownSegment()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync();
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().SplitAsync(source.Session.Id, source.Segments[0].Id));
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service().SplitAsync(source.Session.Id, "unknown"));
    }

    /// <summary>重建服务后可以从投影关系恢复源场次隐藏状态。</summary>
    [Fact]
    public async Task ReconcileRestoresHiddenSourcesAfterRestart()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(10000);
        var grouped = await fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], null);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateManyAsync(session => session.Id == first.Session.Id || session.Id == second.Session.Id,
            Builders<BroadcastSession>.Update.Set(session => session.IsHidden, false));
        await fixture.Service().ReconcileAsync();
        var sources = await fixture.Mongo.Collection<BroadcastSession>("sessions").Find(session => session.Id == first.Session.Id || session.Id == second.Session.Id).ToListAsync();
        Assert.All(sources, session => Assert.True(session.IsHidden));
        Assert.True((await fixture.Library.GetAsync(grouped.Id)).Session.ProjectionReady);
    }
}
