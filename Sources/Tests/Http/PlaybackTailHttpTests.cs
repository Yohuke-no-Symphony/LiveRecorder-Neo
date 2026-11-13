using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证播放、事件与互动统计共用的末尾断流裁剪边界。</summary>
public sealed class PlaybackTailHttpTests
{
    /// <summary>已结束场次只移除末尾空档及其全部事件，保留中间断流且不修改档案结束时间。</summary>
    [MongoHttpFact]
    public async Task CompletedPlaybackExcludesAllTailKindsAndPreservesTheMiddleGap()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, true);
        var events = VisibleEvents().Concat(new[]
        {
            "Danmu", "SuperChat", "Gift", "Guard", "RedPocket", "Gap", "Raw", "Error", "RoomUpdate"
        }.Select(kind => Item("tail-" + kind, kind, 15000, "tail-user"))).ToArray();
        await WriteEventsAsync(fixture, session, events);
        var archive = ArchivePath(fixture, session);
        var raw = await File.ReadAllTextAsync(archive);

        var detail = await DetailAsync(fixture, session);
        Assert.Equal(15000d, detail.DurationMs);
        var gap = Assert.Single(detail.Gaps);
        Assert.Equal(5000d, gap.StartMs);
        Assert.Equal(10000d, gap.EndMs);
        Assert.Equal(session.EndedAtUtc, detail.Session.EndedAtUtc);
        var page = await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?fromMs=0&toMs=20000&limit=100");
        Assert.NotNull(page);
        Assert.Equal(VisibleEvents().Select(item => item.EventId), page.Items.Select(item => item.EventId));
        Assert.Contains(page.Items, item => item.EventId == "middle-gap");
        var tail = await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?fromMs=15000&toMs=20000&limit=100");
        Assert.NotNull(tail);
        Assert.Empty(tail.Items);
        Assert.Null(tail.NextCursor);
        var timeline = await TimelineAsync(fixture, session);
        Assert.Equal(15000d, timeline.DurationMs);
        Assert.Equal(2, timeline.Heat.Sum(bucket => bucket.Count));
        Assert.Equal(new[] { "SuperChat", "Gift" }, timeline.Markers.Select(marker => marker.Kind));
        Assert.All(timeline.Markers, marker => Assert.True(marker.EndOffsetMs < 15000));
        var report = await AnalyticsAsync(fixture, session);
        Assert.Equal(2, report.DanmuCount);
        Assert.Equal(1, report.SuperChatCount);
        Assert.Equal(1, report.GiftCount);
        Assert.Equal(0, report.GuardCount);
        Assert.Equal(0, report.RedPocketCount);
        Assert.Equal(1, report.CoverageGaps);
        Assert.Equal(35m, report.ObservedAmount);
        Assert.Equal(2, report.ActiveUsers);
        var saved = await fixture.Context.Collection<BroadcastSession>("sessions").Find(item => item.Id == session.Id).SingleAsync();
        Assert.Equal(session.EndedAtUtc, saved.EndedAtUtc);
        Assert.Equal(raw, await File.ReadAllTextAsync(archive));
    }

    /// <summary>场次结束后立即替换直播缓存，末尾同身份 SC 补价及礼物连击续报不能回流有效区间。</summary>
    [MongoHttpFact]
    public async Task CompletionRebuildsLiveCachesBeforeFinancialTailUpdatesCanLeakBack()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, false);
        var repeatedPayment = Payment(16000, 300m);
        var comboContinuation = Gift("gift-tail", 17000, 10);
        await WriteEventsAsync(fixture, session, [.. VisibleEvents(), repeatedPayment, comboContinuation]);
        var liveTimeline = await TimelineAsync(fixture, session);
        var liveReport = await AnalyticsAsync(fixture, session);
        Assert.Equal(300m, Assert.Single(liveTimeline.Markers, marker => marker.Kind == "SuperChat").Amount);
        Assert.Equal(10, Assert.Single(liveTimeline.Markers, marker => marker.Kind == "Gift").Quantity);
        Assert.Equal(350m, liveReport.ObservedAmount);
        var endedAt = session.StartedAtUtc.AddSeconds(20);
        await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(item => item.EndedAtUtc, endedAt).Set(item => item.State, "Completed"));

        var completedTimeline = await TimelineAsync(fixture, session);
        var completedReport = await AnalyticsAsync(fixture, session);

        Assert.Equal(15000d, completedTimeline.DurationMs);
        Assert.Equal(30m, Assert.Single(completedTimeline.Markers, marker => marker.Kind == "SuperChat").Amount);
        Assert.Equal(1, Assert.Single(completedTimeline.Markers, marker => marker.Kind == "Gift").Quantity);
        Assert.Equal(35m, completedReport.ObservedAmount);
        Assert.Equal(1, completedReport.GiftQuantity);
        Assert.NotEqual(liveTimeline.Revision, completedTimeline.Revision);
        Assert.NotEqual(liveReport.SourceIdentity, completedReport.SourceIdentity);
        Assert.Equal(endedAt, (await DetailAsync(fixture, session)).Session.EndedAtUtc);
    }

    /// <summary>媒体仍在录制时保留真实时长及末尾事件，不提前套用已结束档案的裁剪。</summary>
    [MongoHttpFact]
    public async Task LivePlaybackRetainsEventsAfterTheLatestCommittedMedia()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, false);
        await fixture.Context.Collection<RecordingSegment>("segments").UpdateOneAsync(item => item.SessionId == session.Id && item.RelativePath == "second.flv",
            Builders<RecordingSegment>.Update.Set(item => item.IsClosed, false));
        await WriteEventsAsync(fixture, session,
        [
            .. VisibleEvents(), Item("live-tail", "Danmu", 18000, "tail-user"), Payment(16000, 300m)
        ]);

        var detail = await DetailAsync(fixture, session);
        var page = await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?fromMs=15000&toMs=20000");
        var timeline = await TimelineAsync(fixture, session);
        var report = await AnalyticsAsync(fixture, session);

        Assert.Null(detail.Session.EndedAtUtc);
        Assert.True(detail.DurationMs > 20000);
        Assert.NotNull(page);
        Assert.Equal(new[] { "live-tail", "superchat" }, page.Items.Select(item => item.EventId));
        Assert.Equal(3, timeline.Heat.Sum(bucket => bucket.Count));
        Assert.Equal(300m, Assert.Single(timeline.Markers, marker => marker.Kind == "SuperChat").Amount);
        Assert.Equal(305m, report.ObservedAmount);
        Assert.Equal(3, report.DanmuCount);
    }

    /// <summary>拆分场次的大负偏移不能因浮点减法舍入重新包含播放终点事件。</summary>
    [MongoHttpFact]
    public async Task SplitProjectionExcludesTheEndEventAfterNegativeOffsetRounding()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SeedSessionAsync(fixture, true);
        var sourceDirectory = Path.Combine("tail-source", session.Id);
        await fixture.Context.Collection<RecordingSegment>("segments").DeleteManyAsync(item => item.SessionId == session.Id);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new RecordingSegment
        {
            SessionId = session.Id, RelativePath = "split.flv", DurationMs = 1000, CommittedBytes = 64, IsClosed = true
        });
        await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(item => item.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(item => item.EventSources,
            [
                new EventArchiveSource { DirectoryPath = sourceDirectory, StartOffsetMs = -100000, FromMs = 100000 }
            ]));
        await using (var writer = new SessionEventWriter(Path.Combine(fixture.StoragePath, sourceDirectory, "弹幕")))
        {
            await writer.WriteAsync(Item("inside", "SuperChat", 100999, "payer"));
            await writer.WriteAsync(Item("boundary", "SuperChat", 101000, "payer"));
            await writer.WriteAsync(Item("after", "SuperChat", 101001, "payer"));
        }

        Assert.Equal(1000d, (await DetailAsync(fixture, session)).DurationMs);
        var page = await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?fromMs=0&toMs=1000");

        Assert.NotNull(page);
        var visible = Assert.Single(page.Items);
        Assert.Equal("inside", visible.EventId);
        Assert.Equal(999d, visible.OffsetMs);
        Assert.Null(page.NextCursor);
    }

    private static async Task<HttpServerFixture> CreateFixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            var supervisor = fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single();
            await supervisor.StopAsync(CancellationToken.None);
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task<BroadcastSession> SeedSessionAsync(HttpServerFixture fixture, bool completed)
    {
        var start = DateTime.UtcNow.AddMinutes(-5);
        start = new(start.Ticks - start.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var session = new BroadcastSession
        {
            RoomId = 100, Title = "尾部断流播放回归", DirectoryPath = Path.Combine("tail-http", Guid.NewGuid().ToString("N")),
            StartedAtUtc = start, EndedAtUtc = completed ? start.AddSeconds(20) : null,
            LastMediaAtUtc = start.AddSeconds(15), State = completed ? "Completed" : "Recording", HasDanmu = true
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertManyAsync(new[]
        {
            new RecordingSegment { SessionId = session.Id, RelativePath = "first.flv", StartOffsetMs = 0, DurationMs = 5000, CommittedBytes = 64, IsClosed = true },
            new RecordingSegment { SessionId = session.Id, RelativePath = "second.flv", StartOffsetMs = 10000, DurationMs = 5000, CommittedBytes = 64, IsClosed = true }
        });
        return session;
    }

    private static RecordedEventEnvelope[] VisibleEvents() =>
    [
        Item("visible-danmu", "Danmu", 4000, "viewer"), Item("middle-gap", "Danmu", 7500, "payer"),
        Payment(12000, 30m), Gift("gift-base", 13000, 1)
    ];

    private static RecordedEventEnvelope Item(string id, string kind, double offset, string user) => new()
    {
        EventId = id, Kind = kind, OffsetMs = offset, UserId = user, UserName = user, Text = id,
        ReceivedAtUtc = DateTime.UtcNow, SourceTimeUtc = DateTime.UtcNow
    };

    private static RecordedEventEnvelope Payment(double offset, decimal amount)
    {
        var item = Item("superchat", "SuperChat", offset, "payer");
        item.PriceSource = "ProtocolPaid";
        item.Amount = amount;
        item.Payload = new() { ["identity"] = "superchat", ["roomId"] = "100" };
        return item;
    }

    private static RecordedEventEnvelope Gift(string id, double offset, int cumulative)
    {
        var item = Item(id, "Gift", offset, "payer");
        item.CoinType = "gold";
        item.PriceSource = "ProtocolGold";
        item.Quantity = cumulative;
        item.Amount = 5m * cumulative;
        item.Payload = new()
        {
            ["roomId"] = "100", ["giftId"] = "1", ["giftName"] = "礼物", ["unitPrice"] = 5000m,
            ["quantity"] = cumulative, ["batchComboId"] = "combo", ["batchComboCount"] = cumulative
        };
        return item;
    }

    private static async Task WriteEventsAsync(HttpServerFixture fixture, BroadcastSession session, params RecordedEventEnvelope[] events)
    {
        await using var writer = new SessionEventWriter(Path.GetDirectoryName(ArchivePath(fixture, session))!);
        foreach (var item in events)
        {
            await writer.WriteAsync(item);
        }
    }

    private static string ArchivePath(HttpServerFixture fixture, BroadcastSession session) =>
        Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕", "事件.jsonl");

    private static async Task<SessionDetail> DetailAsync(HttpServerFixture fixture, BroadcastSession session) =>
        await fixture.Admin.Client.GetFromJsonAsync<SessionDetail>($"/api/sessions/{session.Id}") ?? throw new InvalidDataException("Missing session detail.");

    private static async Task<SessionTimelineResponse> TimelineAsync(HttpServerFixture fixture, BroadcastSession session) =>
        await fixture.Admin.Client.GetFromJsonAsync<SessionTimelineResponse>($"/api/sessions/{session.Id}/timeline") ?? throw new InvalidDataException("Missing session timeline.");

    private static async Task<AnalyticsReport> AnalyticsAsync(HttpServerFixture fixture, BroadcastSession session) =>
        await fixture.Admin.Client.GetFromJsonAsync<AnalyticsReport>($"/api/sessions/{session.Id}/analytics") ?? throw new InvalidDataException("Missing session analytics.");
}
