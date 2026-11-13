using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>播放时间轴的增量归档、类型聚合、金融去重与投影边界回归。</summary>
public sealed class PlaybackTimelineProjectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "live-recorder-timeline-" + Guid.NewGuid().ToString("N"));

    /// <summary>热度只统计弹幕，四类记录分别聚合，协议包及空档不进入热度。</summary>
    [Fact]
    public async Task HeatAndMarkersRetainSeparateEventKinds()
    {
        await AppendAsync("source", Item(1, "Danmu", 100), Item(2, "Danmu", 900), Item(3, "Gift", 200, 2),
            Item(4, "SuperChat", 300, 30), Item(5, "Guard", 400, 138), Item(6, "RedPocket", 500, 999),
            Item(7, "Raw", 600), Item(8, "Gap", 700));
        using var service = new SessionTimelineService();
        var result = await service.GetAsync(Session(), root, 10000);
        Assert.Equal(2, Assert.Single(result.Heat).Count);
        Assert.Equal(new[] { "Gift", "SuperChat", "Guard", "RedPocket" }, result.Markers.Select(marker => marker.Kind));
        Assert.Null(result.Markers.Single(marker => marker.Kind == "RedPocket").Amount);
        Assert.All(result.Markers, marker => Assert.Equal(1, marker.Count));
    }

    /// <summary>跨服务重启恢复字节游标，迟到弹幕进入旧时间桶，重复查询不重复计数。</summary>
    [Fact]
    public async Task RestartResumesCommittedBytesAndIncludesLateEvents()
    {
        await AppendAsync("source", Item(1, "Danmu", 8000));
        SessionTimelineResponse initial;
        using (var service = new SessionTimelineService())
        {
            initial = await service.GetAsync(Session(), root, 10000);
        }
        var committed = (await CacheAsync())["sources"]![0]!["position"]!.GetValue<long>();
        Assert.Equal(new FileInfo(ArchivePath("source")).Length, committed);
        await AppendAsync("source", Item(2, "Danmu", 1000));
        using var restarted = new SessionTimelineService();
        var updated = await restarted.GetAsync(Session(), root, 10000);
        Assert.Equal(new[] { 1000d, 8000d }, updated.Heat.Select(bucket => bucket.OffsetMs));
        Assert.Equal(2, updated.Heat.Sum(bucket => bucket.Count));
        Assert.NotEqual(initial.Revision, updated.Revision);
        var repeated = await restarted.GetAsync(Session(), root, 10000);
        Assert.Equal(updated.Revision, repeated.Revision);
        Assert.Equal(2, repeated.Heat.Sum(bucket => bucket.Count));
        var after = await CacheAsync();
        Assert.True(after["sources"]![0]!["position"]!.GetValue<long>() > committed);
        Assert.Equal(2, after["sources"]![0]!["lastSequence"]!.GetValue<long>());
    }

    /// <summary>尚未换行的追加记录不能消费，后续提交完成时只计一次。</summary>
    [Fact]
    public async Task PartialTailWaitsForCompleteNewline()
    {
        await AppendAsync("source", Item(1, "Danmu", 100));
        using var service = new SessionTimelineService();
        await service.GetAsync(Session(), root, 10000);
        var initialLength = new FileInfo(ArchivePath("source")).Length;
        var serialized = JsonSerializer.Serialize(Item(2, "Danmu", 500), JsonFiles.Options);
        await File.AppendAllTextAsync(ArchivePath("source"), serialized[..(serialized.Length / 2)]);
        var partial = await service.GetAsync(Session(), root, 10000);
        Assert.Equal(1, partial.Heat.Sum(bucket => bucket.Count));
        Assert.Equal(initialLength, (await CacheAsync())["sources"]![0]!["position"]!.GetValue<long>());
        await File.AppendAllTextAsync(ArchivePath("source"), serialized[(serialized.Length / 2)..] + "\n");
        var completed = await service.GetAsync(Session(), root, 10000);
        Assert.Equal(2, completed.Heat.Sum(bucket => bucket.Count));
        Assert.Equal(new FileInfo(ArchivePath("source")).Length, (await CacheAsync())["sources"]![0]!["position"]!.GetValue<long>());
    }

    /// <summary>归组保留真实时间偏移和稳定来源序号，拆分区间右边界不重复。</summary>
    [Fact]
    public async Task ProjectionPreservesGapsAndHalfOpenSourceRanges()
    {
        await AppendAsync("first", Item(1, "Danmu", 999), Item(2, "Gift", 1000, 2), Item(3, "Danmu", 2000));
        await AppendAsync("second", Item(1, "Danmu", 0), Item(2, "Guard", 200, 138));
        var session = new BroadcastSession
        {
            DirectoryPath = "projection",
            EventSources =
            [
                new() { DirectoryPath = "first", FromMs = 1000, ToMs = 2000, StartOffsetMs = -1000 },
                new() { DirectoryPath = "second", FromMs = 0, ToMs = 1000, StartOffsetMs = 10000 }
            ]
        };
        using var service = new SessionTimelineService();
        var result = await service.GetAsync(session, root, 12000);
        Assert.Equal(1, Assert.Single(result.Heat).Count);
        Assert.Equal(10000d, result.Heat[0].OffsetMs);
        Assert.Equal(new[] { 0d, 10200d }, result.Markers.Select(marker => marker.OffsetMs));
        Assert.Equal(1_000_000_000_002L, result.Markers[0].Sequence);
        Assert.Equal(2_000_000_000_002L, result.Markers[1].Sequence);
    }

    /// <summary>投影范围和偏移改变后不能复用同一文件的旧时间桶。</summary>
    [Fact]
    public async Task ChangedProjectionInvalidatesCache()
    {
        await AppendAsync("source", Item(1, "Danmu", 1000), Item(2, "Danmu", 6000));
        var session = new BroadcastSession
        {
            DirectoryPath = "projection", EventSources = [new() { DirectoryPath = "source", FromMs = 0, ToMs = 5000 }]
        };
        using var service = new SessionTimelineService();
        var first = await service.GetAsync(session, root, 15000);
        session.EventSources[0].FromMs = 5000;
        session.EventSources[0].ToMs = 10000;
        session.EventSources[0].StartOffsetMs = 5000;
        var second = await service.GetAsync(session, root, 15000);
        Assert.Equal(1000, Assert.Single(first.Heat).OffsetMs);
        Assert.Equal(11000, Assert.Single(second.Heat).OffsetMs);
        Assert.NotEqual(first.Revision, second.Revision);
    }

    /// <summary>截断或同长度替换原档案时重建，损坏的快照不影响归档恢复。</summary>
    [Fact]
    public async Task TruncationReplacementAndDamagedCacheRebuildSafely()
    {
        await AppendAsync("source", Item(1, "Danmu", 100), Item(2, "Danmu", 200));
        using var service = new SessionTimelineService();
        Assert.Equal(2, (await service.GetAsync(Session(), root, 10000)).Heat.Sum(bucket => bucket.Count));
        await File.WriteAllTextAsync(ArchivePath("source"), "");
        await AppendAsync("source", Item(1, "Gift", 100, 2));
        var rebuilt = await service.GetAsync(Session(), root, 10000);
        Assert.Empty(rebuilt.Heat);
        Assert.Equal("Gift", Assert.Single(rebuilt.Markers).Kind);
        var oldLength = new FileInfo(ArchivePath("source")).Length;
        var replacement = JsonSerializer.Serialize(Item(1, "Gift", 200, 3), JsonFiles.Options) + "\n";
        Assert.Equal(oldLength, System.Text.Encoding.UTF8.GetByteCount(replacement));
        await File.WriteAllTextAsync(ArchivePath("source"), replacement);
        File.SetLastWriteTimeUtc(ArchivePath("source"), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(3m, Assert.Single((await service.GetAsync(Session(), root, 10000)).Markers).Amount);
        foreach (var invalid in new[] { "{truncated", "{\"sources\":null}", "{\"sources\":[null]}", "{\"sources\":[{\"markers\":{\"broken\":null}}]}" })
        {
            await File.WriteAllTextAsync(CachePath("source"), invalid);
            var restored = await service.GetAsync(Session(), root, 10000);
            Assert.Empty(restored.Heat);
            Assert.Equal(3m, Assert.Single(restored.Markers).Amount);
        }
    }

    /// <summary>红包各阶段、重复 SC 和大航海报价不会重复成多笔记录或支付。</summary>
    [Fact]
    public async Task DuplicateFinancialObservationsMergeWithoutQuotedMoney()
    {
        var pocket = Item(1, "RedPocket", 100, 100);
        pocket.TransactionId = "lot-1";
        var drawn = Item(2, "RedPocket", 2100, 100);
        drawn.TransactionId = "lot-1";
        var sc = Item(3, "SuperChat", 300, 30);
        sc.EventId = "same-sc";
        var repeatedSc = Item(4, "SuperChat", 400, 30);
        repeatedSc.EventId = "same-sc";
        var quote = Item(5, "Guard", 500, 1000);
        quote.EventId = "same-guard";
        quote.PriceSource = "ProtocolQuote";
        var paid = Item(6, "Guard", 600, 138);
        paid.EventId = "same-guard";
        await AppendAsync("source", pocket, drawn, sc, repeatedSc, quote);
        using var service = new SessionTimelineService();
        Assert.Null((await service.GetAsync(Session(), root, 10000)).Markers.Single(marker => marker.Kind == "Guard").Amount);
        await AppendAsync("source", paid);
        var after = await service.GetAsync(Session(), root, 10000);
        Assert.Equal(3, after.Markers.Count);
        Assert.All(after.Markers, marker => Assert.Equal(1, marker.Count));
        Assert.Equal(30m, after.Markers.Single(marker => marker.Kind == "SuperChat").Amount);
        Assert.Equal(138m, after.Markers.Single(marker => marker.Kind == "Guard").Amount);
        var red = after.Markers.Single(marker => marker.Kind == "RedPocket");
        Assert.Equal(100d, red.OffsetMs);
        Assert.Equal(2100d, red.EndOffsetMs);
        Assert.Null(red.Amount);
    }

    /// <summary>拆分前建立连击基线，范围内只计新增数量；重复元数据可补齐未知价格。</summary>
    [Fact]
    public async Task GiftComboSeedsBaselineAndOnlyAddsIncrementalQuantity()
    {
        var prefix = Gift(1, 1000, 4, 4, null);
        var inside = Gift(2, 5000, 2, 6, null);
        var duplicate = Gift(3, 6000, 2, 6, 1);
        duplicate.EventId = inside.EventId;
        await AppendAsync("source", prefix, inside, duplicate);
        var session = new BroadcastSession
        {
            DirectoryPath = "projection", EventSources = [new() { DirectoryPath = "source", FromMs = 5000, ToMs = 10000, StartOffsetMs = -5000 }]
        };
        using var service = new SessionTimelineService();
        var marker = Assert.Single((await service.GetAsync(session, root, 5000)).Markers);
        Assert.Equal(0, marker.OffsetMs);
        Assert.Equal(1, marker.Count);
        Assert.Equal(2, marker.Quantity);
        Assert.Equal(2m, marker.Amount);
    }

    /// <summary>范围之前的已知购买在拆分之后重推时不重复出现。</summary>
    [Fact]
    public async Task RepeatedPurchaseAfterSplitDoesNotCreateNewMarker()
    {
        var initial = Item(1, "SuperChat", 1000, 30);
        initial.EventId = "same";
        var duplicate = Item(2, "SuperChat", 6000, 30);
        duplicate.EventId = "same";
        await AppendAsync("source", initial, duplicate);
        var session = new BroadcastSession
        {
            DirectoryPath = "projection", EventSources = [new() { DirectoryPath = "source", FromMs = 5000, ToMs = 10000, StartOffsetMs = -5000 }]
        };
        using var service = new SessionTimelineService();
        Assert.Empty((await service.GetAsync(session, root, 5000)).Markers);
    }

    /// <summary>长场次按请求桶数限制响应大小，首条时间和计数仍完整。</summary>
    [Fact]
    public async Task DenseTimelineHasBoundedGroupsWithoutLosingCounts()
    {
        var items = Enumerable.Range(1, 500).SelectMany(index => new[]
        {
            Item(index * 2 - 1, "Danmu", index * 2000),
            Item(index * 2, new[] { "Gift", "SuperChat", "Guard", "RedPocket" }[index % 4], index * 2000, 1)
        }).ToArray();
        await AppendAsync("source", items);
        using var service = new SessionTimelineService();
        var result = await service.GetAsync(Session(), root, 1_100_000, 20);
        Assert.True(result.Heat.Count <= 20);
        Assert.True(result.Markers.Count <= 80);
        Assert.Equal(500, result.Heat.Sum(bucket => bucket.Count));
        Assert.Equal(500, result.Markers.Sum(marker => marker.Count));
        Assert.All(result.Markers, marker => Assert.InRange(marker.OffsetMs, 0, result.DurationMs));
        Assert.Contains(result.Markers, marker => marker.Count > 1);
        var zero = await service.GetAsync(Session(), root, 0);
        Assert.Empty(zero.Heat);
        Assert.Empty(zero.Markers);
    }

    /// <summary>混合时间桶保留已识别金额，未知记录不伪装成完整购买总额。</summary>
    [Fact]
    public async Task MixedKnownAndUnknownRecordsPreserveKnownAmount()
    {
        var known = Item(1, "Gift", 100, 2);
        var unknown = Item(2, "Gift", 200);
        unknown.PriceSource = "Unknown";
        await AppendAsync("source", known, unknown);
        using var service = new SessionTimelineService();
        var marker = Assert.Single((await service.GetAsync(Session(), root, 10000)).Markers);
        Assert.Equal(2, marker.Count);
        Assert.Equal(2, marker.Quantity);
        Assert.Equal(2m, marker.Amount);
    }

    /// <summary>多个客户端并发查询同一场次及不同场次时不会重复消费或混用快照。</summary>
    [Fact]
    public async Task ConcurrentQueriesKeepIndependentCompleteSnapshots()
    {
        await AppendAsync("source", Item(1, "Danmu", 100));
        await AppendAsync("other", Item(1, "Gift", 100, 3));
        using var service = new SessionTimelineService();
        var queries = Enumerable.Range(0, 12).Select(index => service.GetAsync(new() { DirectoryPath = index % 2 == 0 ? "source" : "other" }, root, 10000)).ToArray();
        var results = await Task.WhenAll(queries);
        for (var index = 0; index < results.Length; index++)
        {
            if (index % 2 == 0)
            {
                Assert.Equal(1, Assert.Single(results[index].Heat).Count);
                Assert.Empty(results[index].Markers);
            }
            else
            {
                Assert.Empty(results[index].Heat);
                Assert.Equal(3m, Assert.Single(results[index].Markers).Amount);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private static BroadcastSession Session() => new() { DirectoryPath = "source" };
    private string ArchivePath(string directory) => Path.Combine(root, directory, "弹幕", "事件.jsonl");
    private string CachePath(string directory) => Path.Combine(root, directory, "分析", "timeline.json");
    private async Task<JsonObject> CacheAsync() => JsonNode.Parse(await File.ReadAllTextAsync(CachePath("source")))!.AsObject();

    private async Task AppendAsync(string directory, params RecordedEventEnvelope[] items)
    {
        var path = ArchivePath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var item in items)
        {
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(item, JsonFiles.Options) + "\n");
        }
    }

    private static RecordedEventEnvelope Item(long sequence, string kind, double offsetMs, decimal? amount = null) => new()
    {
        Sequence = sequence, EventId = $"{kind}-{sequence}", Kind = kind, OffsetMs = offsetMs,
        UserId = "1", UserName = "用户甲", Text = "记录正文", Amount = amount,
        CoinType = kind == "Gift" ? "gold" : null, PriceSource = kind == "Gift" ? "ProtocolGold" : "ProtocolPaid"
    };

    private static RecordedEventEnvelope Gift(long sequence, double offset, int quantity, long cumulative, decimal? unit)
    {
        var item = Item(sequence, "Gift", offset, unit * quantity);
        item.Quantity = quantity;
        item.PriceSource = unit is null ? "Unknown" : "ProtocolGold";
        item.Payload = new() { ["giftId"] = "flower", ["giftName"] = "小花花", ["batchComboId"] = "combo", ["batchComboCount"] = cumulative };
        if (unit is { } value)
        {
            item.Payload["unitPrice"] = value * 1000;
        }
        return item;
    }
}
