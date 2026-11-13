using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证文件统计版本、范围缓存身份及拆分前的连击去重基线。</summary>
public sealed class AnalyticsFileTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "live-recorder-analytics-" + Guid.NewGuid().ToString("N"));

    /// <summary>旧版本统计即使大小未变、刚刚创建，也必须重算。</summary>
    [Fact]
    public async Task FreshLegacySummaryCannotReuseIncorrectMoney()
    {
        await WriteAsync("source", AnalyticsFinancialTests.Gift("a", "gift", 0.1m));
        var source = Path.Combine(directory, "source");
        var output = Path.Combine(source, "分析", "summary.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            sourceBytes = new FileInfo(Path.Combine(source, "弹幕", "事件.jsonl")).Length,
            builtAtUtc = DateTime.UtcNow,
            paidRevenue = 999m
        }, JsonFiles.Options));
        var report = await new AnalyticsService(new()).GetAsync(source);
        Assert.Equal(0.1m, report.ObservedAmount);
        Assert.Equal(AnalyticsReport.CURRENT_SCHEMA_VERSION, report.SchemaVersion);
        Assert.NotEmpty(report.SourceFingerprint);
        Assert.NotEmpty(report.SourceIdentity);
    }

    /// <summary>旧版本缓存即使指纹和生成时间仍有效，也必须补算分类和独立用户榜。</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacySummaryIsRebuiltWithCategoriesAndUserRankings(int version)
    {
        await WriteAsync("source", AnalyticsFinancialTests.Gift("gift", "gift", 2m, 3));
        var source = Path.Combine(directory, "source");
        var service = new AnalyticsService(new());
        var saved = await service.GetAsync(source);
        saved.SchemaVersion = version;
        saved.Categories.Clear();
        saved.UserRankings.Clear();
        saved.PayingUsers = 999;
        await JsonFiles.WriteAsync(Path.Combine(source, "分析", "summary.json"), saved);

        var report = await service.GetAsync(source);
        Assert.Equal(AnalyticsReport.CURRENT_SCHEMA_VERSION, report.SchemaVersion);
        Assert.Equal(6m, Assert.Single(report.Categories, category => category.Kind == "Gift").Amount);
        Assert.Equal(3, Assert.Single(report.UserRankings["giftQuantity"]).GiftQuantity);
        Assert.Equal(1, report.PayingUsers);
        Assert.Equal(saved.SourceFingerprint, report.SourceFingerprint);
    }

    /// <summary>版本和源指纹相同的半成品缓存，不能把新统计维度永久显示为零。</summary>
    [Theory]
    [InlineData("legacy-shape")]
    [InlineData("missing-category")]
    [InlineData("missing-ranking")]
    [InlineData("null-ranking")]
    [InlineData("null-categories")]
    [InlineData("zero-buckets")]
    [InlineData("incorrect-category-amount")]
    [InlineData("null-category-entry")]
    [InlineData("null-bucket-entry")]
    [InlineData("null-users")]
    [InlineData("null-ranking-user")]
    [InlineData("incorrect-payer-count")]
    [InlineData("incorrect-danmu-user-count")]
    public async Task SameVersionIncompleteSummaryIsRebuilt(string corruption)
    {
        await WriteAsync("source",
            new RecordedEventEnvelope
                { EventId = "danmu", UserId = "1", UserName = "user", Kind = "Danmu", OffsetMs = 1000 },
            AnalyticsFinancialTests.Gift("gift", "gift", 2m, 3));
        var source = Path.Combine(directory, "source");
        var service = new AnalyticsService(new());
        var expected = await service.GetAsync(source);
        var saved = JsonSerializer.SerializeToNode(expected, JsonFiles.Options)!.AsObject();
        switch (corruption)
        {
            case "legacy-shape":
                saved["categories"] = new System.Text.Json.Nodes.JsonArray();
                saved["userRankings"] = new System.Text.Json.Nodes.JsonObject();
                saved["payingUsers"] = 0;
                saved["danmuUsers"] = 0;
                break;
            case "missing-category":
                saved["categories"]!.AsArray().RemoveAt(0);
                break;
            case "missing-ranking":
                saved["userRankings"]!.AsObject().Remove("giftQuantity");
                break;
            case "null-ranking":
                saved["userRankings"]!["danmuCount"] = null;
                break;
            case "null-categories":
                saved["categories"] = null;
                break;
            case "zero-buckets":
                foreach (var bucket in saved["buckets"]!.AsArray())
                {
                    bucket!["danmuCount"] = 0;
                }

                break;
            case "incorrect-category-amount":
                saved["categories"]!.AsArray()
                    .Single(category => category!["kind"]!.GetValue<string>() == "Gift")!["amount"] = 0;
                break;
            case "null-category-entry":
                saved["categories"]![0] = null;
                break;
            case "null-bucket-entry":
                saved["buckets"]![0] = null;
                break;
            case "null-users":
                saved["topUsers"] = null;
                break;
            case "null-ranking-user":
                saved["userRankings"]!["danmuCount"]![0] = null;
                break;
            case "incorrect-payer-count":
                saved["payingUsers"] = 0;
                break;
            case "incorrect-danmu-user-count":
                saved["danmuUsers"] = 0;
                break;
        }

        await JsonFiles.WriteAsync(Path.Combine(source, "分析", "summary.json"), saved);

        var report = await service.GetAsync(source);
        Assert.Equal(expected.SourceIdentity, report.SourceIdentity);
        Assert.Equal(expected.SourceFingerprint, report.SourceFingerprint);
        Assert.Equal(expected.ObservedAmount, report.ObservedAmount);
        Assert.Equal(3, report.Categories.Count);
        Assert.Equal(6m, Assert.Single(report.Categories, category => category.Kind == "Gift").Amount);
        Assert.Equal(3, Assert.Single(report.UserRankings["giftQuantity"]).GiftQuantity);
        Assert.NotNull(report.UserRankings["danmuCount"]);
        Assert.Equal(1, report.PayingUsers);
        Assert.Equal(1, report.DanmuUsers);
        Assert.Equal(1, report.Buckets.Sum(bucket => bucket.DanmuCount));
    }

    /// <summary>完整的非空缓存及合法空归档仍可复用，不因零值反复扫描源文件。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteSummaryRemainsReusable(bool empty)
    {
        if (empty)
        {
            await WriteAsync("source");
        }
        else
        {
            await WriteAsync("source", AnalyticsFinancialTests.Gift("gift", "gift", 2m));
        }

        var source = Path.Combine(directory, "source");
        var service = new AnalyticsService(new());
        var saved = await service.GetAsync(source);
        saved.BuiltAtUtc = DateTime.UtcNow.AddHours(-1);
        await JsonFiles.WriteAsync(Path.Combine(source, "分析", "summary.json"), saved);

        var report = await service.GetAsync(source);
        Assert.Equal(saved.BuiltAtUtc, report.BuiltAtUtc);
        Assert.Equal(saved.SourceFingerprint, report.SourceFingerprint);
        Assert.Equal(saved.ObservedAmount, report.ObservedAmount);
        Assert.Equal(3, report.Categories.Count);
        Assert.Equal(9, report.UserRankings.Count);
    }

    /// <summary>范围及投影偏移的变化不能复用同一物理文件的旧缓存。</summary>
    [Fact]
    public async Task SameFileProjectionRangeAndOffsetChangeInvalidatesCache()
    {
        var first = AnalyticsFinancialTests.Gift("first", "gift", 2m);
        first.OffsetMs = 1000;
        var second = AnalyticsFinancialTests.Gift("second", "gift", 5m);
        second.OffsetMs = 61000;
        await WriteAsync("source", first, second);
        var session = Projection(0, 30000, 0);
        var service = new AnalyticsService(new());
        var initial = await service.GetAsync(session, directory);
        Assert.Equal(2m, initial.ObservedAmount);
        session.EventSources[0].FromMs = 30000;
        session.EventSources[0].ToMs = 90000;
        session.EventSources[0].StartOffsetMs = 59000;
        var next = await service.GetAsync(session, directory);
        Assert.Equal(5m, next.ObservedAmount);
        Assert.NotEqual(initial.SourceIdentity, next.SourceIdentity);
        Assert.Equal(120000d, Assert.Single(next.Buckets).OffsetMs);
    }

    /// <summary>从连击中间拆开时只计范围内的累计差值，右边界采用右开区间。</summary>
    [Fact]
    public async Task SplitProjectionSeedsComboPrefixAndExcludesRightBoundary()
    {
        var prefix = AnalyticsFinancialTests.Gift("before", "gift", 10m, combo: "combo", cumulative: 4);
        prefix.OffsetMs = 1000;
        var included = AnalyticsFinancialTests.Gift("inside", "gift", 10m, combo: "combo", cumulative: 6);
        included.OffsetMs = 5000;
        var boundary = AnalyticsFinancialTests.Gift("end", "gift", 10m, combo: "combo", cumulative: 7);
        boundary.OffsetMs = 10000;
        await WriteAsync("source", prefix, included, boundary);
        var report = await new AnalyticsService(new()).GetAsync(Projection(5000, 10000, -5000), directory);
        Assert.Equal(20m, report.ObservedAmount);
        Assert.Equal(2, report.GiftQuantity);
        Assert.Equal(1, report.GiftCount);
        Assert.Equal(0, Assert.Single(report.Buckets).OffsetMs);
    }

    /// <summary>范围前的已付包在范围内重推时不能重复计入支付。</summary>
    [Fact]
    public async Task PaymentRepeatAfterSplitRetainsPrefixDeduplication()
    {
        var prefix = AnalyticsFinancialTests.Guard("same", "ProtocolPaid", 138m);
        prefix.OffsetMs = 1000;
        var repeated = AnalyticsFinancialTests.Guard("same", "ProtocolPaid", 138m);
        repeated.OffsetMs = 6000;
        await WriteAsync("source", prefix, repeated);
        var report = await new AnalyticsService(new()).GetAsync(Projection(5000, null, -5000), directory);
        Assert.Equal(0m, report.ObservedAmount);
        Assert.Equal(0m, report.ProtocolPaidAmount);
    }

    /// <summary>裁切起点之前已出现的红包，不因范围内开奖阶段重推再次增加发包数量。</summary>
    [Fact]
    public async Task RedPocketStagesAfterSplitRetainPrefixDeduplication()
    {
        var prefix = new RecordedEventEnvelope
            { EventId = "start", Kind = "RedPocket", TransactionId = "same", UserId = "1", OffsetMs = 1000 };
        var result = new RecordedEventEnvelope
            { EventId = "result", Kind = "RedPocket", TransactionId = "same", UserId = "1", OffsetMs = 6000 };
        await WriteAsync("source", prefix, result);

        var report = await new AnalyticsService(new()).GetAsync(Projection(5000, null, -5000), directory);
        Assert.Equal(0, report.RedPocketCount);
        Assert.Empty(report.UserRankings["redPocketCount"]);
        Assert.Empty(report.TopUsers);
        Assert.Empty(report.Buckets);
    }

    /// <summary>损坏的统计缓存只影响缓存本身，原始文件仍可重建。</summary>
    [Fact]
    public async Task InvalidSummaryIsRebuiltFromEventFile()
    {
        await WriteAsync("source", AnalyticsFinancialTests.Gift("a", "gift", 0.001m));
        var source = Path.Combine(directory, "source");
        var output = Path.Combine(source, "分析", "summary.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "{truncated");
        var report = await new AnalyticsService(new()).GetAsync(source);
        Assert.Equal(0.001m, report.ObservedAmount);
        var saved = JsonSerializer.Deserialize<AnalyticsReport>(await File.ReadAllTextAsync(output), JsonFiles.Options);
        Assert.Equal(0.001m, saved!.ObservedAmount);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static BroadcastSession Projection(double from, double? to, double offset) => new()
    {
        DirectoryPath = "projection",
        EventSources = [new() { DirectoryPath = "source", FromMs = from, ToMs = to, StartOffsetMs = offset }]
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
