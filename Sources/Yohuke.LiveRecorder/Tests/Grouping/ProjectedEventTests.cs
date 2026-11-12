using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Grouping.Tests;

/// <summary>验证归组、拆分后的事件时间投影和稳定分页。</summary>
public sealed class ProjectedEventTests
{
    /// <summary>同一原始事件只能落入拆分的一侧，分界点事件属于右侧零时刻。</summary>
    [Fact]
    public async Task SplitBoundaryEventOccursExactlyOnce()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        await fixture.WriteEventsAsync(source.Session, 0, 999, 1000, 1001, 1999);
        var split = await fixture.Service().SplitAsync(source.Session.Id, source.Segments[1].Id);
        var reader = new SessionEventReader(new EventArchiveReader());
        var left = await reader.ReadAsync(split[0], fixture.Root, 0, 2000, null, null, 100);
        var right = await reader.ReadAsync(split[1], fixture.Root, 0, 2000, null, null, 100);
        Assert.Equal(new[] { 0d, 999d }, left.Items.Select(item => item.OffsetMs));
        Assert.Equal(new[] { 0d, 1d, 999d }, right.Items.Select(item => item.OffsetMs));
        Assert.Equal(5, left.Items.Concat(right.Items).Select(item => item.EventId).Distinct().Count());
        Assert.Equal(5, left.Items.Count + right.Items.Count);
    }

    /// <summary>跨来源分页的游标在读者重建后仍稳定，事件 UTC 空档不被压缩。</summary>
    [Fact]
    public async Task GroupCursorSurvivesReaderRestartWithoutDuplicates()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(10000);
        await fixture.WriteEventsAsync(first.Session, 0, 300, 800);
        await fixture.WriteEventsAsync(second.Session, 0, 100);
        var grouped = await fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], null);
        var actual = new List<RecordedEventEnvelope>();
        long? cursor = null;
        for (var index = 0; index < 10; index++)
        {
            var reader = new SessionEventReader(new EventArchiveReader());
            var page = await reader.ReadAsync(grouped, fixture.Root, 0, 20000, null, cursor, 2);
            actual.AddRange(page.Items);
            if (page.NextCursor is null)
            {
                break;
            }
            Assert.True(page.NextCursor > (cursor ?? 0));
            cursor = page.NextCursor;
        }
        Assert.Equal(new[] { 0d, 300d, 800d, 10000d, 10100d }, actual.Select(item => item.OffsetMs));
        Assert.Equal(5, actual.Select(item => item.Sequence).Distinct().Count());
        Assert.Equal(5, actual.Select(item => item.EventId).Distinct().Count());
        var repeated = await new SessionEventReader(new EventArchiveReader()).ReadAsync(grouped, fixture.Root, 0, 20000, null, null, 100);
        Assert.Equal(actual.Select(item => item.Sequence), repeated.Items.Select(item => item.Sequence));
    }

    /// <summary>嵌套归组和再次拆分不重复应用事件 offset。</summary>
    [Fact]
    public async Task NestedProjectionKeepsOriginalEventTimeMapping()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(10000);
        await fixture.WriteEventsAsync(first.Session, 100, 1700);
        await fixture.WriteEventsAsync(second.Session, 50, 1500);
        var grouped = await fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], null);
        var detail = await fixture.Library.GetAsync(grouped.Id);
        var boundary = detail.Segments.First(segment => segment.StartOffsetMs >= 10000);
        var split = await fixture.Service().SplitAsync(grouped.Id, boundary.Id);
        var regrouped = await fixture.Service().GroupAsync(split[0].Id, [split[1].Id], null);
        var events = await new SessionEventReader(new EventArchiveReader()).ReadAsync(regrouped, fixture.Root, 0, 20000, null, null, 100);
        Assert.Equal(new[] { 100d, 1700d, 10050d, 11500d }, events.Items.Select(item => item.OffsetMs));
        Assert.Equal(4, events.Items.Select(item => item.EventId).Distinct().Count());
    }
}
