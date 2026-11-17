using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证文件服务与独立节点共用的基线恢复、来源投影及输入隔离。</summary>
public sealed class AnalyticsProjectionEngineTests
{
    /// <summary>先恢复裁切之前的完整去重基线，再处理到达顺序早于基线的选区事件。</summary>
    [Fact]
    public async Task CroppedProjectionRestoresBaselineBeforeOutOfOrderSelectedRows()
    {
        var beforeGift = AnalyticsFinancialTests.Gift("before-gift", "gift", 10m, combo: "combo", cumulative: 4);
        beforeGift.OffsetMs = 20000;
        var selectedGift = AnalyticsFinancialTests.Gift("selected-gift", "gift", 10m, combo: "combo", cumulative: 6);
        selectedGift.OffsetMs = 40000;
        RecordedEventEnvelope[] events =
        [
            selectedGift,
            Payment("old-payment", 50000, 100m),
            Danmu("translation", 60000, "EN: hello"),
            Online(50000, 200),
            Payment("new-payment", 65000, 30m),
            beforeGift,
            Payment("old-payment", 20000, 100m),
            Online(10000, 100),
            Danmu("viewer", 70000),
            Payment("right-boundary", 90000, 999m)
        ];

        var result = await new AnalyticsProjectionEngine().BuildAsync(
        [
            new() { OffsetMs = -30000, FromMs = 30000, ToMs = 90000, ReadAsync = token => ReadAsync(events, token) }
        ], new()
        {
            FixedEndMs = 60000,
            TranslationPattern = new("^EN:", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            CaptureContributions = true
        });

        var report = result.Report;
        Assert.Equal(0, report.FromMs);
        Assert.Equal(60000, report.ToMs);
        Assert.Equal(20m, report.GoldGiftValue);
        Assert.Equal(2, report.GiftQuantity);
        Assert.Equal(30m, report.ProtocolPaidAmount);
        Assert.Equal(1, report.SuperChatCount);
        Assert.Equal(1, report.DanmuCount);
        Assert.Equal(1, report.Translations.Count);
        Assert.Equal(60000, report.Online.ObservedMs);
        Assert.Equal(166.6666666667, report.Online.Average!.Value, 8);
        Assert.Equal(200, report.Online.Peak);
    }

    /// <summary>多个采集来源各自恢复在线基线，并投影到同一逻辑场次时间轴。</summary>
    [Fact]
    public async Task SourcesKeepIndependentOnlineBaselinesAndProjectLogicalOffsets()
    {
        var first = new[] { Online(55000, 100), Danmu("first", 58000) };
        var second = new[] { Online(15000, 300), Danmu("second", 20000) };

        var report = (await new AnalyticsProjectionEngine().BuildAsync(
        [
            new() { ToMs = 60000, ReadAsync = token => ReadAsync(first, token) },
            new() { OffsetMs = 120000, ToMs = 60000, ReadAsync = token => ReadAsync(second, token) }
        ], new() { FixedEndMs = 180000 })).Report;

        Assert.Equal(2, report.DanmuCount);
        Assert.Equal(180000, report.Online.RangeMs);
        Assert.Equal(50000, report.Online.ObservedMs);
        Assert.Equal(280, report.Online.Average);
        Assert.Equal(new[] { (55000d, 60000d), (135000d, 180000d) },
            report.Online.Intervals.Select(interval => (interval.FromMs, interval.ToMs)));
        Assert.Equal(new[] { 0d, 120000d }, report.Buckets.Select(bucket => bucket.OffsetMs));
    }

    /// <summary>重复使用固定快照时原事件及序号保持不变，贡献导出不能累加来源偏移。</summary>
    [Fact]
    public async Task ReplayingProjectionDoesNotChangeSourceEventsOrExportSequence()
    {
        var events = new[] { Danmu("message", 1000), Payment("payment", 2000, 30m), Online(0, 100) };
        for (var index = 0; index < events.Length; index++)
        {
            events[index].Sequence = 100 + index;
        }
        events[0].TextCode = "event-text";
        events[0].TextArguments = ["argument"];
        var original = JsonSerializer.Serialize(events);
        AnalyticsProjectionSource[] sources =
        [
            new() { OffsetMs = 60000, ToMs = 60000, ReadAsync = token => ReadAsync(events, token) }
        ];
        var engine = new AnalyticsProjectionEngine();
        var options = new AnalyticsProjectionOptions { FixedEndMs = 120000, CaptureContributions = true };

        var first = (await engine.BuildAsync(sources, options)).Report;
        var second = (await engine.BuildAsync(sources, options)).Report;

        Assert.Equal(original, JsonSerializer.Serialize(events));
        Assert.Equal(1, first.DanmuCount);
        Assert.Equal(first.DanmuCount, second.DanmuCount);
        Assert.Equal(30m, second.ProtocolPaidAmount);
        Assert.Equal(60000, Assert.Single(second.Buckets).OffsetMs);
        Assert.Equal(60000, second.Online.ObservedMs);
    }

    /// <summary>活动范围可由事件延伸，固定播放终点始终排除右边界及尾部事件。</summary>
    [Fact]
    public async Task OpenProjectionUsesInitialEndAndObservedEventsWithoutExtendingFixedEnd()
    {
        var events = new[] { Danmu("message", 40000), Danmu("tail", 60000) };
        AnalyticsProjectionSource[] sources = [new() { ReadAsync = token => ReadAsync(events, token) }];
        var engine = new AnalyticsProjectionEngine();

        var open = (await engine.BuildAsync(sources, new() { InitialEndMs = 50000 })).Report;
        var closed = (await engine.BuildAsync(sources, new() { FixedEndMs = 60000, InitialEndMs = 90000 })).Report;

        Assert.Equal(60001, open.ToMs);
        Assert.Equal(2, open.DanmuCount);
        Assert.Equal(60000, closed.ToMs);
        Assert.Equal(1, closed.DanmuCount);
    }

    private static async IAsyncEnumerable<RecordedEventEnvelope> ReadAsync(IEnumerable<RecordedEventEnvelope> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static RecordedEventEnvelope Online(double offsetMs, long count) => new()
    {
        Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = offsetMs,
        RawJson = new JsonObject { ["data"] = new JsonObject { ["online_count"] = count } }.ToJsonString()
    };

    private static RecordedEventEnvelope Danmu(string id, double offsetMs, string text = "hello") => new()
    {
        EventId = id, Kind = "Danmu", UserId = "viewer", OffsetMs = offsetMs, Text = text
    };

    private static RecordedEventEnvelope Payment(string id, double offsetMs, decimal amount) => new()
    {
        EventId = id, Kind = "SuperChat", UserId = "payer", OffsetMs = offsetMs, Amount = amount, PriceSource = "ProtocolPaid"
    };
}
