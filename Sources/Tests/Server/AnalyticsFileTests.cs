using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证文件统计版本、范围缓存身份及拆分前的连击去重基线。</summary>
public sealed class AnalyticsFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "live-recorder-analytics-" + Guid.NewGuid().ToString("N"));

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
