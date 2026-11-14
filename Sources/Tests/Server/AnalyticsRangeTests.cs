using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证选区统计重新去重、分钟桶关联、来源投影及独立缓存边界。</summary>
public sealed class AnalyticsRangeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "live-recorder-analytics-range-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SelectionRecountsUsersPaymentsAndTranslationsWithoutReplacingFullCache()
    {
        var prefixGift = AnalyticsFinancialTests.Gift("gift-before", "gift", 10m, combo: "combo", cumulative: 4);
        prefixGift.OffsetMs = 20000;
        var selectedGift = AnalyticsFinancialTests.Gift("gift-inside", "gift", 10m, combo: "combo", cumulative: 6);
        selectedGift.OffsetMs = 40000;
        await WriteAsync("source", Online(0, 100), Danmu("repeat", "before", 10000), Payment("old-sc", 20000, 100m), prefixGift,
            Danmu("repeat", "before", 40000), Danmu("one", "viewer", 40000), selectedGift, Payment("sc-one", 45000, 30m),
            Online(50000, 200), Payment("old-sc", 50000, 100m), Danmu("translation", "translator", 60000, "EN: translated"),
            Payment("sc-two", 65000, 60m), Danmu("two", "viewer", 70000), Payment("right-boundary", 90000, 999m));
        var session = Session("source", 180000);
        var service = new AnalyticsService(new());
        var full = await service.GetAsync(session, directory, 180000, "^EN:");
        var cachePath = Path.Combine(directory, "source", "分析", "summary.json");
        var originalCache = await File.ReadAllTextAsync(cachePath);

        var report = await service.GetAsync(session, directory, 180000, "^EN:", 30000, 90000);

        Assert.Equal(30000, report.FromMs);
        Assert.Equal(90000, report.ToMs);
        Assert.Equal(2, report.DanmuCount);
        Assert.Equal(1, report.DanmuUsers);
        Assert.Equal(2, report.SuperChatCount);
        Assert.Equal(90m, report.ProtocolPaidAmount);
        Assert.Equal(20m, report.GoldGiftValue);
        Assert.Equal(2, report.GiftQuantity);
        Assert.Equal(3, report.ActiveUsers);
        Assert.Equal(2, report.PayingUsers);
        Assert.Equal(1, report.Translations.Count);
        Assert.Equal("translator", Assert.Single(report.Translations.TopUsers).UserId);
        Assert.Equal(60000, report.Online.ObservedMs);
        Assert.Equal(1, report.Online.CoverageRate);
        Assert.Equal(166.6666666667, report.Online.Average!.Value, 8);
        Assert.Equal(200, report.Online.Peak);
        Assert.Equal(2, report.Buckets.Count);
        Assert.All(report.Buckets, bucket => Assert.Equal(30000, bucket.DurationMs));
        Assert.Equal(1, report.Buckets[0].SuperChatCount);
        Assert.Equal(30m, report.Buckets[0].SuperChatAmount);
        Assert.Equal(1, report.Buckets[1].SuperChatCount);
        Assert.Equal(60m, report.Buckets[1].SuperChatAmount);
        Assert.Equal(133.3333333333, report.Buckets[0].OnlineAverage!.Value, 8);
        Assert.Equal(200, report.Buckets[1].OnlineAverage);
        Assert.NotEqual(full.SourceIdentity, report.SourceIdentity);
        Assert.Equal(originalCache, await File.ReadAllTextAsync(cachePath));
    }

    [Fact]
    public async Task ProjectionDoesNotCarryOnlineCountsBetweenOriginalArchives()
    {
        await WriteAsync("first", Online(55000, 100));
        await WriteAsync("second", Online(15000, 300));
        var session = Session("group", 180000);
        session.EventSources =
        [
            new() { DirectoryPath = "first", FromMs = 0, ToMs = 60000 },
            new() { DirectoryPath = "second", FromMs = 0, ToMs = 60000, StartOffsetMs = 120000 }
        ];

        var report = await new AnalyticsService(new()).GetAsync(session, directory, 180000, "^EN:");

        Assert.Equal(180000, report.Online.RangeMs);
        Assert.Equal(50000, report.Online.ObservedMs);
        Assert.Equal(280, report.Online.Average);
        Assert.Equal(2, report.Online.Intervals.Count);
        Assert.Equal((55000d, 60000d), (report.Online.Intervals[0].FromMs, report.Online.Intervals[0].ToMs));
        Assert.Equal((135000d, 180000d), (report.Online.Intervals[1].FromMs, report.Online.Intervals[1].ToMs));
        Assert.Equal(new[] { 0d, 120000d }, report.Buckets.Select(bucket => bucket.OffsetMs));
    }

    [Fact]
    public async Task SplitOnlineBaselineRespectsDisconnectAndRightBoundary()
    {
        await WriteAsync("source", Online(10000, 100),
            new() { Kind = "Gap", Command = "RECORDER_DISCONNECTED", OffsetMs = 25000 },
            Online(50000, 200), Online(70000, 999));
        var session = Session("split", 40000);
        session.EventSources = [new() { DirectoryPath = "source", FromMs = 30000, ToMs = 70000, StartOffsetMs = -30000 }];

        var report = await new AnalyticsService(new()).GetAsync(session, directory, 40000, "^EN:");

        var interval = Assert.Single(report.Online.Intervals);
        Assert.Equal((20000d, 40000d), (interval.FromMs, interval.ToMs));
        Assert.Equal(200, report.Online.Peak);
        Assert.Equal(0.5, report.Online.CoverageRate);
    }

    [Fact]
    public async Task RangeOnlyRequestDoesNotCreateFullCacheAndClampsToPlayback()
    {
        await WriteAsync("source", Online(0, 0), Payment("right-boundary", 10000, 100m));
        var service = new AnalyticsService(new());
        var session = Session("source", 60000);

        var report = await service.GetAsync(session, directory, 10000, "^EN:", 5000, 60000);

        Assert.Equal(5000, report.FromMs);
        Assert.Equal(10000, report.ToMs);
        Assert.Equal(0, report.Online.Peak);
        Assert.Equal(0, report.Online.Average);
        Assert.Equal(5000, report.Online.ObservedMs);
        Assert.Equal(0, report.SuperChatCount);
        Assert.False(File.Exists(Path.Combine(directory, "source", "分析", "summary.json")));
        var empty = await service.GetAsync(session, directory, 10000, "^EN:", 20000, 30000);
        Assert.Equal(10000, empty.FromMs);
        Assert.Equal(10000, empty.ToMs);
        Assert.Null(empty.Online.Average);
        Assert.Equal(0, empty.Online.CoverageRate);
        Assert.Empty(empty.Buckets);
    }

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(1000, 999)]
    [InlineData(double.NaN, 1000)]
    [InlineData(0, double.PositiveInfinity)]
    public async Task InvalidSelectionIsRejected(double fromMs, double toMs)
    {
        await Assert.ThrowsAsync<InfrastructureException>(() => new AnalyticsService(new()).GetAsync(
            Session("source", 60000), directory, 60000, "^EN:", fromMs, toMs));
    }

    [Theory]
    [InlineData("missing-online")]
    [InlineData("null-online")]
    [InlineData("missing-range")]
    [InlineData("missing-peak")]
    [InlineData("missing-interval-count")]
    [InlineData("incorrect-average")]
    [InlineData("incorrect-observed")]
    [InlineData("incorrect-coverage")]
    [InlineData("overlapping-intervals")]
    [InlineData("null-interval")]
    [InlineData("missing-bucket-sc-count")]
    [InlineData("incorrect-bucket-sc-count")]
    [InlineData("missing-bucket-online")]
    [InlineData("incorrect-bucket-online")]
    [InlineData("incorrect-bucket-duration")]
    [InlineData("missing-online-bucket")]
    public async Task IncompleteOrInconsistentOnlineCacheIsRebuilt(string corruption)
    {
        await WriteAsync("source", Online(0, 100), Payment("sc", 10000, 30m));
        var service = new AnalyticsService(new());
        var session = Session("source", 90000);
        var original = await service.GetAsync(session, directory, 90000, "^EN:");
        var saved = JsonSerializer.SerializeToNode(original, JsonFiles.Options)!.AsObject();
        var online = saved["online"]!.AsObject();
        var bucket = saved["buckets"]![0]!.AsObject();
        switch (corruption)
        {
            case "missing-online":
                saved.Remove("online");
                break;
            case "null-online":
                saved["online"] = null;
                break;
            case "missing-range":
                saved.Remove("toMs");
                break;
            case "missing-peak":
                online.Remove("peak");
                break;
            case "missing-interval-count":
                online["intervals"]![0]!.AsObject().Remove("count");
                break;
            case "incorrect-average":
                online["average"] = 999;
                break;
            case "incorrect-observed":
                online["observedMs"] = 999;
                break;
            case "incorrect-coverage":
                online["coverageRate"] = 1;
                break;
            case "overlapping-intervals":
                online["intervals"]!.AsArray().Add(online["intervals"]![0]!.DeepClone());
                break;
            case "null-interval":
                online["intervals"]![0] = null;
                break;
            case "missing-bucket-sc-count":
                bucket.Remove("superChatCount");
                break;
            case "incorrect-bucket-sc-count":
                bucket["superChatCount"] = 0;
                break;
            case "missing-bucket-online":
                bucket.Remove("onlineAverage");
                break;
            case "incorrect-bucket-online":
                bucket["onlineObservedMs"] = 0;
                break;
            case "incorrect-bucket-duration":
                bucket["durationMs"] = 1;
                break;
            case "missing-online-bucket":
                saved["buckets"]!.AsArray().Clear();
                break;
        }
        await JsonFiles.WriteAsync(Path.Combine(directory, "source", "分析", "summary.json"), saved);

        var rebuilt = await service.GetAsync(session, directory, 90000, "^EN:");

        Assert.Equal(100, rebuilt.Online.Peak);
        Assert.Equal(100, rebuilt.Online.Average);
        Assert.Equal(60000, rebuilt.Online.ObservedMs);
        Assert.Equal(2d / 3, rebuilt.Online.CoverageRate, 10);
        Assert.Single(rebuilt.Online.Intervals);
        Assert.Equal(1, Assert.Single(rebuilt.Buckets).SuperChatCount);
    }

    [Fact]
    public async Task MissingOnlineDataRemainsUnknownAndValidCacheCanBeReused()
    {
        await WriteAsync("source", Danmu("one", "viewer", 1000));
        var service = new AnalyticsService(new());
        var session = Session("source", 60000);
        var original = await service.GetAsync(session, directory, 60000, "^EN:");
        original.BuiltAtUtc = DateTime.UtcNow.AddHours(-1);
        await JsonFiles.WriteAsync(Path.Combine(directory, "source", "分析", "summary.json"), original);

        var report = await service.GetAsync(session, directory, 60000, "^EN:");

        Assert.Equal(original.BuiltAtUtc, report.BuiltAtUtc);
        Assert.Null(report.Online.Peak);
        Assert.Null(report.Online.Average);
        Assert.Equal(0, report.Online.ObservedMs);
        Assert.Equal(60000, report.Online.RangeMs);
        Assert.Null(Assert.Single(report.Buckets).OnlineAverage);
    }

    [Fact]
    public async Task CompleteOnlineCacheWithFractionalIntervalsRemainsReusable()
    {
        await WriteAsync("source", Online(0.125, 101), Online(33333.333, 201), Online(70000.625, 301));
        var service = new AnalyticsService(new());
        var session = Session("source", 180000);
        var original = await service.GetAsync(session, directory, 180000, "^EN:");
        original.BuiltAtUtc = DateTime.UtcNow.AddHours(-1);
        await JsonFiles.WriteAsync(Path.Combine(directory, "source", "分析", "summary.json"), original);

        var report = await service.GetAsync(session, directory, 180000, "^EN:");

        Assert.Equal(original.BuiltAtUtc, report.BuiltAtUtc);
        Assert.Equal(original.Online.ObservedMs, report.Online.ObservedMs);
        Assert.Equal(original.Online.Average, report.Online.Average);
        Assert.Equal(3, report.Buckets.Count);
    }

    [Fact]
    public async Task LiveCacheExpiresEvenWhenArchiveHasNoNewEvents()
    {
        await WriteAsync("source", Online(0, 100));
        var service = new AnalyticsService(new());
        var session = new BroadcastSession { DirectoryPath = "source", StartedAtUtc = DateTime.UtcNow.AddMinutes(-2) };
        var original = await service.GetAsync(session, directory, 1000, "^EN:");
        original.BuiltAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await JsonFiles.WriteAsync(Path.Combine(directory, "source", "分析", "summary.json"), original);

        var report = await service.GetAsync(session, directory, 1000, "^EN:");

        Assert.True(report.BuiltAtUtc > original.BuiltAtUtc);
        Assert.Equal(original.SourceFingerprint, report.SourceFingerprint);
        Assert.Equal(original.SourceIdentity, report.SourceIdentity);
        Assert.True(report.ToMs >= 120000);
        Assert.Equal(60000, report.Online.ObservedMs);
        Assert.InRange(report.Online.CoverageRate, 0, 0.5);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static BroadcastSession Session(string relative, double durationMs)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new() { DirectoryPath = relative, StartedAtUtc = start, EndedAtUtc = start.AddMilliseconds(durationMs) };
    }

    private static RecordedEventEnvelope Online(double offsetMs, long count) => new()
    {
        Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = offsetMs,
        Payload = new JsonObject { ["data"] = new JsonObject { ["online_count"] = count } }
    };

    private static RecordedEventEnvelope Danmu(string id, string userId, double offsetMs, string text = "hello") => new()
    {
        EventId = id, Kind = "Danmu", UserId = userId, OffsetMs = offsetMs, Text = text
    };

    private static RecordedEventEnvelope Payment(string id, double offsetMs, decimal amount) => new()
    {
        EventId = id, Kind = "SuperChat", UserId = "payer", OffsetMs = offsetMs, Amount = amount, PriceSource = "ProtocolPaid"
    };

    private async Task WriteAsync(string relative, params RecordedEventEnvelope[] events)
    {
        await using var writer = new SessionEventWriter(Path.Combine(directory, relative, "弹幕"));
        foreach (var item in events)
        {
            await writer.WriteAsync(item);
        }
    }
}
