using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证同传与互动统计的分流、去重、来源范围及规则缓存。</summary>
public sealed class AnalyticsTranslationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "live-recorder-translations-" + Guid.NewGuid().ToString("N"));

    /// <summary>只剔除同传弹幕，译者的普通发言与付费行为继续参与互动统计。</summary>
    [Fact]
    public void TranslationsAreSeparatedBeforeCountingUsersRankingsAndBuckets()
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Danmu("ordinary", "1"));
        accumulator.Add(Danmu("translator", "2", 60000), true);
        accumulator.Add(Danmu("mixed-translation", "3"), true);
        accumulator.Add(Danmu("mixed-ordinary", "3"));
        accumulator.Add(Danmu("supporter-translation", "4", 61000), true);
        accumulator.Add(new()
        {
            EventId = "sc", Kind = "SuperChat", UserId = "4", UserName = "译者4", Text = "【付费留言】",
            OffsetMs = 61000, Amount = 30m, PriceSource = "ProtocolPaid"
        }, true);
        var gift = AnalyticsFinancialTests.Gift("gift", "gift", 2m, 3);
        gift.UserId = "4";
        accumulator.Add(gift);

        var report = accumulator.Build();

        Assert.Equal(2, report.DanmuCount);
        Assert.Equal(2, report.DanmuUsers);
        Assert.Equal(3, report.ActiveUsers);
        Assert.Equal(1, report.PayingUsers);
        Assert.Equal(36m, report.ObservedAmount);
        Assert.Equal(3, report.GiftQuantity);
        Assert.Equal(1, report.SuperChatCount);
        Assert.DoesNotContain(report.TopUsers, user => user.UserId == "2");
        Assert.Equal(new[] { "1", "3" }, report.UserRankings["danmuCount"].Select(user => user.UserId));
        Assert.Equal(2, report.Buckets.Sum(bucket => bucket.DanmuCount));
        Assert.Equal(4, report.Buckets.Sum(bucket => bucket.Messages));
        var secondMinute = report.Buckets[1];
        Assert.Equal(60000, secondMinute.OffsetMs);
        Assert.Equal(1, secondMinute.ActiveUsers);
        Assert.Equal(0, secondMinute.DanmuUsers);
        Assert.Equal(3, report.Translations.Count);
        Assert.Equal(3, report.Translations.Users);
        Assert.Equal(new[] { 1, 2 }, report.Translations.Buckets.Select(bucket => bucket.Count));
        Assert.Equal(new[] { "2", "3", "4" }, report.Translations.TopUsers.Select(user => user.UserId));
    }

    /// <summary>重复事件全局去重且保留身份补齐，匿名无事件ID的同传逐条计数。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedEventCannotAppearInBothStatistics(bool translationFirst)
    {
        var accumulator = new AnalyticsAccumulator();
        accumulator.Add(Danmu("same", ""), translationFirst);
        accumulator.Add(Danmu("same", "7"), !translationFirst);
        accumulator.Add(Danmu("same", "7"), true);
        accumulator.Add(Danmu("", ""), true);
        accumulator.Add(Danmu("", "0"), true);

        var first = accumulator.Build();
        Assert.Equal(0, first.DanmuCount);
        Assert.Equal(0, first.ActiveUsers);
        Assert.Empty(first.Buckets);
        Assert.Equal(3, first.Translations.Count);
        Assert.Equal(1, first.Translations.Users);
        Assert.Equal(1, Assert.Single(first.Translations.TopUsers).Count);
        Assert.Equal("7", Assert.Single(first.Translations.TopUsers).UserId);
        Assert.Equal(3, Assert.Single(first.Translations.Buckets).Count);

        accumulator.Add(Danmu("next", "7", 60000), true);
        Assert.Equal(4, accumulator.Build().Translations.Count);
        Assert.Equal(3, first.Translations.Count);
    }

    /// <summary>榜单限制前百但总量与人数保持完整，排序先按条数再按稳定UID。</summary>
    [Fact]
    public void TranslatorRankingLimitDoesNotTruncateTotals()
    {
        var accumulator = new AnalyticsAccumulator();
        for (var index = 1; index <= 101; index++)
        {
            accumulator.Add(Danmu($"translation-{index}", index.ToString("D3")), true);
        }
        accumulator.Add(Danmu("extra", "101"), true);

        var report = accumulator.Build().Translations;
        Assert.Equal(102, report.Count);
        Assert.Equal(101, report.Users);
        Assert.Equal(100, report.TopUsers.Count);
        Assert.Equal("101", report.TopUsers[0].UserId);
        Assert.Equal(2, report.TopUsers[0].Count);
        Assert.Equal("001", report.TopUsers[1].UserId);
        Assert.Equal(102, report.Buckets.Sum(bucket => bucket.Count));
    }

    /// <summary>规则变化无需等待三十秒，缓存保持单文件并可复用相同口径。</summary>
    [Fact]
    public async Task EffectivePatternInvalidatesFreshCacheWithoutChangingSourceFiles()
    {
        await WriteAsync("source", Danmu("en1", "1", text: "EN: first"), Danmu("en2", "1", text: "EN: second"),
            Danmu("jp", "2", text: "JP: translation"), Danmu("ordinary", "3", text: "普通弹幕"));
        var service = new AnalyticsService(new());
        var session = new BroadcastSession { DirectoryPath = "source" };

        var english = await service.GetAsync(session, directory, 180000, "^EN:");
        var japanese = await service.GetAsync(session, directory, 180000, "^JP:");
        Assert.Equal(2, english.DanmuCount);
        Assert.Equal(2, english.Translations.Count);
        Assert.Equal(3, japanese.DanmuCount);
        Assert.Equal(1, japanese.Translations.Count);
        Assert.NotEqual(english.SourceIdentity, japanese.SourceIdentity);
        Assert.Equal(english.SourceFingerprint, japanese.SourceFingerprint);

        japanese.BuiltAtUtc = DateTime.UtcNow.AddHours(-1);
        var cachePath = Path.Combine(directory, "source", "分析", "summary.json");
        await JsonFiles.WriteAsync(cachePath, japanese);
        var reused = await service.GetAsync(session, directory, 180000, "^JP:");
        Assert.Equal(japanese.BuiltAtUtc, reused.BuiltAtUtc);
        var restored = await service.GetAsync(session, directory, 180000, "^EN:");
        Assert.Equal(english.SourceIdentity, restored.SourceIdentity);
        Assert.Equal(2, restored.Translations.Count);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(cachePath)!));
    }

    /// <summary>缺少同传数据或内部计数不一致的同版本缓存必须重算。</summary>
    [Theory]
    [InlineData("missing-translations")]
    [InlineData("null-translations")]
    [InlineData("missing-buckets")]
    [InlineData("missing-bucket-offset")]
    [InlineData("missing-user-name")]
    [InlineData("null-bucket")]
    [InlineData("duplicate-bucket")]
    [InlineData("incorrect-count")]
    [InlineData("incorrect-users")]
    [InlineData("null-user")]
    [InlineData("incorrect-ranking")]
    public async Task IncompleteTranslationSummaryIsRebuilt(string corruption)
    {
        await WriteAsync("source", Danmu("translation", "1", 61000, "【同传】"));
        var service = new AnalyticsService(new());
        var session = new BroadcastSession { DirectoryPath = "source" };
        var original = await service.GetAsync(session, directory, 180000, "【");
        var saved = JsonSerializer.SerializeToNode(original, JsonFiles.Options)!.AsObject();
        switch (corruption)
        {
            case "missing-translations":
                saved.Remove("translations");
                break;
            case "null-translations":
                saved["translations"] = null;
                break;
            case "missing-buckets":
                saved["translations"]!.AsObject().Remove("buckets");
                break;
            case "missing-bucket-offset":
                saved["translations"]!["buckets"]![0]!.AsObject().Remove("offsetMs");
                break;
            case "missing-user-name":
                saved["translations"]!["topUsers"]![0]!.AsObject().Remove("userName");
                break;
            case "null-bucket":
                saved["translations"]!["buckets"]![0] = null;
                break;
            case "duplicate-bucket":
                saved["translations"]!["buckets"]!.AsArray().Add(saved["translations"]!["buckets"]![0]!.DeepClone());
                break;
            case "incorrect-count":
                saved["translations"]!["count"] = 99;
                break;
            case "incorrect-users":
                saved["translations"]!["users"] = 99;
                break;
            case "null-user":
                saved["translations"]!["topUsers"]![0] = null;
                break;
            case "incorrect-ranking":
                saved["translations"]!["topUsers"]![0]!["count"] = 99;
                break;
        }
        await JsonFiles.WriteAsync(Path.Combine(directory, "source", "分析", "summary.json"), saved);

        var rebuilt = await service.GetAsync(session, directory, 180000, "【");
        Assert.Equal(1, rebuilt.Translations.Count);
        Assert.Equal(1, rebuilt.Translations.Users);
        Assert.Equal(1, Assert.Single(rebuilt.Translations.TopUsers).Count);
        Assert.Equal(1, Assert.Single(rebuilt.Translations.Buckets).Count);
        Assert.Equal(60000, Assert.Single(rebuilt.Translations.Buckets).OffsetMs);
    }

    /// <summary>同传应用归组来源裁切、投影偏移和结束场次右开边界，付费基线不受影响。</summary>
    [Fact]
    public async Task TranslationBucketsRespectProjectionAndPlaybackBoundaries()
    {
        var prefix = AnalyticsFinancialTests.Gift("before", "gift", 10m, combo: "combo", cumulative: 4);
        prefix.OffsetMs = 1000;
        var inside = AnalyticsFinancialTests.Gift("inside", "gift", 10m, combo: "combo", cumulative: 6);
        inside.OffsetMs = 61000;
        await WriteAsync("source", prefix, Danmu("before", "1", 59999, "【之前】"),
            Danmu("included", "2", 60000, "【范围内】"), inside,
            Danmu("boundary", "3", 120000, "【播放终点】"));
        var session = new BroadcastSession
        {
            DirectoryPath = "projection", StartedAtUtc = DateTime.UtcNow.AddMinutes(-3), EndedAtUtc = DateTime.UtcNow,
            EventSources = [new() { DirectoryPath = "source", FromMs = 60000, ToMs = 180000, StartOffsetMs = -60000 }]
        };
        var report = await new AnalyticsService(new()).GetAsync(session, directory, 60000, "【");
        Assert.Equal(0, report.DanmuCount);
        Assert.Equal(20m, report.ObservedAmount);
        Assert.Equal(2, report.GiftQuantity);
        Assert.Equal(1, report.Translations.Count);
        Assert.Equal("2", Assert.Single(report.Translations.TopUsers).UserId);
        Assert.Equal(0, Assert.Single(report.Translations.Buckets).OffsetMs);
    }

    /// <summary>匹配超时不能写入半成品缓存，之前的有效快照仍可读取。</summary>
    [Fact]
    public async Task RegexTimeoutDoesNotReplaceValidCachedReport()
    {
        await WriteAsync("source", Danmu("translation", "1", text: new string('a', 100000) + "!"));
        var session = new BroadcastSession { DirectoryPath = "source" };
        var service = new AnalyticsService(new());
        await service.GetAsync(session, directory, 180000, "^EN:");
        var cachePath = Path.Combine(directory, "source", "分析", "summary.json");
        var cached = await File.ReadAllTextAsync(cachePath);

        await Assert.ThrowsAsync<RegexMatchTimeoutException>(() => service.GetAsync(session, directory, 180000, "(a+)+$"));
        Assert.Equal(cached, await File.ReadAllTextAsync(cachePath));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static RecordedEventEnvelope Danmu(string id, string userId, double offsetMs = 0, string text = "同传") => new()
    {
        EventId = id, Kind = "Danmu", UserId = userId, UserName = "译者" + userId, OffsetMs = offsetMs, Text = text
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
