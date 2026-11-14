using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证选区统计的 HTTP 参数、原始来源边界及整场缓存隔离。</summary>
public sealed class AnalyticsRangeHttpTests
{
    /// <summary>负数、逆序、空区间、越界、非有限及非数字参数均返回四百，且不创建统计缓存。</summary>
    [MongoHttpFact]
    public async Task InvalidRangesReturnBadRequestWithoutCreatingCache()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, 120000);
        foreach (var query in new[]
        {
            "fromMs=-1&toMs=1000", "fromMs=120000", "fromMs=120001", "toMs=120001", "toMs=0",
            "fromMs=1000&toMs=999", "fromMs=1000&toMs=1000", "fromMs=NaN", "toMs=NaN",
            "fromMs=Infinity", "toMs=Infinity", "fromMs=invalid", "toMs=invalid"
        })
        {
            using var response = await fixture.Admin.Client.GetAsync($"/api/sessions/{session.Id}/analytics?{query}");
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Query '{query}' returned {response.StatusCode}.");
        }
        Assert.False(File.Exists(CachePath(fixture, session)));
    }

    /// <summary>选区重新计算普通弹幕、SC 去重和在线时间权重，同时保留原整场磁盘缓存。</summary>
    [MongoHttpFact]
    public async Task SelectionRecalculatesInteractionsAndOnlineWithoutReplacingFullCache()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, 180000);
        await WriteAsync(fixture, session.DirectoryPath,
            Online(0, 100), Danmu("before", "before-user", 10000), Payment("old-sc", 20000, 30m),
            Danmu("before", "before-user", 40000), Danmu("inside-one", "viewer", 40000),
            Danmu("inside-one", "viewer", 45000), Payment("sc-one", 45000, 30m),
            Online(50000, 200), Payment("old-sc", 50000, 30m),
            Danmu("translation", "translator", 60000, "【选区中的同传】"), Payment("sc-two", 65000, 60m),
            Danmu("inside-two", "viewer", 70000), Danmu("right-boundary", "late-user", 90000),
            Payment("sc-boundary", 90000, 300m),
            new() { Kind = "Gap", Command = "RECORDER_DISCONNECTED", OffsetMs = 95000 }, Online(130000, 300),
            Payment("playback-boundary", 180000, 9999m));
        var full = await ReportAsync(fixture, session);
        var cache = await File.ReadAllTextAsync(CachePath(fixture, session));

        var selected = await ReportAsync(fixture, session, "fromMs=30000&toMs=90000");

        Assert.Equal(0, full.FromMs);
        Assert.Equal(180000, full.ToMs);
        Assert.Equal(4, full.DanmuCount);
        Assert.Equal(4, full.SuperChatCount);
        Assert.Equal(420m, full.ProtocolPaidAmount);
        Assert.Equal(145000, full.Online.ObservedMs);
        Assert.Equal(200, full.Online.Average);
        Assert.Equal(300, full.Online.Peak);
        Assert.Equal(30000, selected.FromMs);
        Assert.Equal(90000, selected.ToMs);
        Assert.Equal(2, selected.DanmuCount);
        Assert.Equal(1, selected.DanmuUsers);
        Assert.Equal(2, selected.ActiveUsers);
        Assert.Equal(1, selected.PayingUsers);
        Assert.Equal(2, selected.SuperChatCount);
        Assert.Equal(90m, selected.ProtocolPaidAmount);
        Assert.Equal(1, selected.Translations.Count);
        Assert.Equal("translator", Assert.Single(selected.Translations.TopUsers).UserId);
        Assert.Equal(60000, selected.Online.RangeMs);
        Assert.Equal(60000, selected.Online.ObservedMs);
        Assert.Equal(1, selected.Online.CoverageRate);
        Assert.Equal(166.6666666667, selected.Online.Average!.Value, 8);
        Assert.Equal(200, selected.Online.Peak);
        Assert.Equal(new[] { 0d, 60000d }, selected.Buckets.Select(bucket => bucket.OffsetMs));
        Assert.All(selected.Buckets, bucket => Assert.Equal(30000, bucket.DurationMs));
        Assert.All(selected.Buckets, bucket => Assert.Equal(1, bucket.SuperChatCount));
        Assert.Equal(new[] { 30m, 60m }, selected.Buckets.Select(bucket => bucket.SuperChatAmount));
        Assert.NotEqual(full.SourceIdentity, selected.SourceIdentity);
        Assert.Equal(full.SourceFingerprint, selected.SourceFingerprint);

        var firstMinute = await ReportAsync(fixture, session, "toMs=60000");
        Assert.Equal(0, firstMinute.FromMs);
        Assert.Equal(60000, firstMinute.ToMs);
        Assert.Equal(2, firstMinute.SuperChatCount);
        Assert.Equal(60m, firstMinute.ProtocolPaidAmount);
        var lastMinute = await ReportAsync(fixture, session, "fromMs=120000");
        Assert.Equal(120000, lastMinute.FromMs);
        Assert.Equal(180000, lastMinute.ToMs);
        Assert.Equal(50000, lastMinute.Online.ObservedMs);
        Assert.Equal(300, lastMinute.Online.Average);
        Assert.Equal(0, lastMinute.SuperChatCount);
        Assert.Equal(cache, await File.ReadAllTextAsync(CachePath(fixture, session)));
        var reused = await ReportAsync(fixture, session);
        Assert.Equal(full.SourceIdentity, reused.SourceIdentity);
        Assert.Equal(full.BuiltAtUtc, reused.BuiltAtUtc);
        Assert.Equal(420m, reused.ProtocolPaidAmount);
    }

    /// <summary>直播时长覆盖当前真实时间，在线及互动保留已提交媒体之后的有效观测。</summary>
    [MongoHttpFact]
    public async Task LiveStatisticsUseElapsedTimeAndAllowRangesBeyondCommittedMedia()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, 60000, false);
        await WriteAsync(fixture, session.DirectoryPath, Online(0, 100), Online(90000, 200),
            Danmu("live-danmu", "viewer", 90001), Payment("live-sc", 110000, 30m));
        var detail = await fixture.Admin.Client.GetFromJsonAsync<SessionDetail>($"/api/sessions/{session.Id}");

        var full = await ReportAsync(fixture, session);
        var selected = await ReportAsync(fixture, session, "fromMs=90000&toMs=110001");

        Assert.NotNull(detail);
        Assert.Null(detail.Session.EndedAtUtc);
        Assert.True(detail.DurationMs > 120000);
        Assert.True(full.ToMs >= detail.DurationMs);
        Assert.Equal(full.ToMs, full.Online.RangeMs);
        Assert.Equal(120000, full.Online.ObservedMs);
        Assert.Equal(200, full.Online.Peak);
        Assert.Equal(1, full.DanmuCount);
        Assert.Equal(1, full.SuperChatCount);
        Assert.Equal(30m, full.ProtocolPaidAmount);
        Assert.Equal(90000, selected.FromMs);
        Assert.Equal(110001, selected.ToMs);
        Assert.Equal(20001, selected.Online.ObservedMs);
        Assert.Equal(1, selected.Online.CoverageRate);
        Assert.Equal(200, selected.Online.Average);
        Assert.Equal(1, selected.DanmuCount);
        Assert.Equal(1, selected.SuperChatCount);
        Assert.Equal(30m, selected.ProtocolPaidAmount);
    }

    /// <summary>拆分来源采用共享录像坐标，起点前的断连会截断基线，右边界事件不进入选区。</summary>
    [MongoHttpFact]
    public async Task SplitSourcePreservesDisconnectBaselineAndAbsoluteRangeCoordinates()
    {
        await using var fixture = await CreateFixtureAsync();
        var source = "analytics-range-source-" + Guid.NewGuid().ToString("N");
        var session = await SeedSessionAsync(fixture, 40000, sources:
        [
            new() { DirectoryPath = source, StartOffsetMs = -30000, FromMs = 30000, ToMs = 70000 }
        ]);
        await WriteAsync(fixture, source, Online(10000, 100),
            new() { Kind = "Gap", Command = "RECORDER_DISCONNECTED", OffsetMs = 25000 },
            Danmu("inside", "viewer", 45000), Online(50000, 200), Payment("inside-sc", 59999, 30m),
            Payment("range-boundary", 60000, 60m), Online(70000, 999), Payment("playback-boundary", 70000, 999m));

        var report = await ReportAsync(fixture, session, "fromMs=10000&toMs=30000");

        Assert.Equal(10000, report.FromMs);
        Assert.Equal(30000, report.ToMs);
        Assert.Equal(1, report.DanmuCount);
        Assert.Equal(1, report.SuperChatCount);
        Assert.Equal(30m, report.ProtocolPaidAmount);
        Assert.Equal(10000, report.Online.ObservedMs);
        Assert.Equal(0.5, report.Online.CoverageRate);
        var interval = Assert.Single(report.Online.Intervals);
        Assert.Equal((20000d, 30000d, 200L), (interval.FromMs, interval.ToMs, interval.Count));
        Assert.Equal(20000, Assert.Single(report.Buckets).DurationMs);
        Assert.False(File.Exists(CachePath(fixture, session)));
    }

    /// <summary>浮点投影后的时间恰好等于右边界时，源坐标的舍入误差不得让互动重新进入选区。</summary>
    [MongoHttpFact]
    public async Task ProjectedRightBoundaryIsRecheckedAfterFloatingPointAddition()
    {
        await using var fixture = await CreateFixtureAsync();
        var source = "analytics-range-rounded-" + Guid.NewGuid().ToString("N");
        var session = await SeedSessionAsync(fixture, 120000, sources:
        [
            new() { DirectoryPath = source, StartOffsetMs = 60000.1, FromMs = 0, ToMs = 30000 }
        ]);
        var offset = Math.BitDecrement(61000.2 - 60000.1);
        Assert.Equal(61000.2, offset + 60000.1);
        await WriteAsync(fixture, source, Payment("rounded-boundary", offset, 30m));

        var report = await ReportAsync(fixture, session, "fromMs=60000&toMs=61000.2");

        Assert.Equal(0, report.SuperChatCount);
        Assert.Equal(0m, report.ProtocolPaidAmount);
        Assert.Empty(report.Buckets);
    }

    private static async Task<HttpServerFixture> CreateFixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task<BroadcastSession> SeedSessionAsync(HttpServerFixture fixture, double durationMs,
        bool completed = true, List<EventArchiveSource>? sources = null)
    {
        var start = DateTime.UtcNow.AddMinutes(-5);
        start = new(start.Ticks - start.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var session = new BroadcastSession
        {
            RoomId = 100, Title = "选区统计回归", DirectoryPath = "analytics-range-" + Guid.NewGuid().ToString("N"),
            StartedAtUtc = start, EndedAtUtc = completed ? start.AddMilliseconds(durationMs + 30000) : null,
            State = completed ? "Completed" : "Recording", HasDanmu = true, EventSources = sources ?? []
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id, RelativePath = "recording.flv", DurationMs = durationMs,
            CommittedBytes = 64, IsClosed = completed
        });
        return session;
    }

    private static RecordedEventEnvelope Online(double offsetMs, long count) => new()
    {
        Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = offsetMs,
        Payload = new JsonObject { ["data"] = new JsonObject { ["online_count"] = count } }
    };

    private static RecordedEventEnvelope Danmu(string id, string userId, double offsetMs, string text = "普通弹幕") => new()
    {
        EventId = id, Kind = "Danmu", UserId = userId, Text = text, OffsetMs = offsetMs
    };

    private static RecordedEventEnvelope Payment(string id, double offsetMs, decimal amount) => new()
    {
        EventId = id, Kind = "SuperChat", UserId = "payer", OffsetMs = offsetMs, Amount = amount, PriceSource = "ProtocolPaid"
    };

    private static async Task WriteAsync(HttpServerFixture fixture, string relative, params RecordedEventEnvelope[] events)
    {
        await using var writer = new SessionEventWriter(Path.Combine(fixture.StoragePath, relative, "弹幕"));
        foreach (var item in events)
        {
            await writer.WriteAsync(item);
        }
    }

    private static async Task<AnalyticsReport> ReportAsync(HttpServerFixture fixture, BroadcastSession session, string? query = null) =>
        await fixture.Admin.Client.GetFromJsonAsync<AnalyticsReport>($"/api/sessions/{session.Id}/analytics" + (query is null ? "" : "?" + query))
        ?? throw new InvalidDataException("Missing analytics response.");

    private static string CachePath(HttpServerFixture fixture, BroadcastSession session) =>
        Path.Combine(fixture.StoragePath, session.DirectoryPath, "分析", "summary.json");
}
