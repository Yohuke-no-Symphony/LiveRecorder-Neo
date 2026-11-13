using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证分析统计排除同传、账号规则隔离以及权限和本地化错误。</summary>
public sealed class AnalyticsTranslationHttpTests
{
    /// <summary>同一归档按当前账号规则生成统计，规则修改和继承立即生效且缓存不会跨账号串用。</summary>
    [MongoHttpFact]
    public async Task AnalyticsExcludeOnlyMatchingDanmuAndKeepEffectivePatternsIsolated()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var normal = Danmu("normal", "101", "普通弹幕", 500);
        normal.UserName = "【仅用户名匹配】";
        normal.RawJson = "{\"text\":\"【仅原始协议匹配】\"}";
        var superChat = Danmu("super-chat", "203", "【付费留言】", 5000);
        superChat.Kind = "SuperChat";
        superChat.PriceSource = "ProtocolPaid";
        superChat.Amount = 30m;
        var gift = Danmu("gift", "203", "【礼物正文】", 5500);
        gift.Kind = "Gift";
        gift.CoinType = "gold";
        gift.PriceSource = "ProtocolGold";
        gift.Amount = 2m;
        gift.Payload = new() { ["roomId"] = "100", ["giftId"] = "1", ["unitPrice"] = 2000m, ["quantity"] = 1 };
        var session = await SeedSessionAsync(fixture,
            normal,
            Danmu("translation-first", "201", "译者【第一句】后缀", 1000),
            Danmu("translation-first", "201", "译者【第一句】后缀", 1000),
            Danmu("translation-second", "201", "【第二句】", 65000),
            Danmu("mixed-translation", "202", "【混合用户同传】", 2000),
            Danmu("mixed-normal", "202", "混合用户普通弹幕", 3000),
            Danmu("paying-translation", "203", "【付费用户同传】", 4000),
            superChat,
            gift,
            Danmu("english", "204", "EN: personal translation", 6000),
            Danmu("japanese", "205", "JP: global translation", 7000),
            Danmu("playback-boundary", "201", "【播放终点之外】", 180000));
        var initial = await ReportAsync(fixture.Admin, session);

        Assert.Equal(4, initial["danmuCount"]!.GetValue<int>());
        Assert.Equal(4, initial["danmuUsers"]!.GetValue<int>());
        Assert.Equal(5, initial["activeUsers"]!.GetValue<int>());
        Assert.Equal(1, initial["payingUsers"]!.GetValue<int>());
        Assert.Equal(1, initial["superChatCount"]!.GetValue<int>());
        Assert.Equal(1, initial["giftCount"]!.GetValue<int>());
        Assert.Equal(32m, initial["observedAmount"]!.GetValue<decimal>());
        var translations = initial["translations"]!.AsObject();
        Assert.Equal(4, translations["count"]!.GetValue<int>());
        Assert.Equal(3, translations["users"]!.GetValue<int>());
        Assert.Equal(new[] { "201", "202", "203" }, TranslationUsers(initial).Order(StringComparer.Ordinal));
        var leadingTranslator = translations["topUsers"]!.AsArray().First()!;
        Assert.Equal("201", leadingTranslator["userId"]!.GetValue<string>());
        Assert.Equal("用户201", leadingTranslator["userName"]!.GetValue<string>());
        Assert.Equal(2, leadingTranslator["count"]!.GetValue<int>());
        Assert.Equal(new[] { (Offset: 0d, Count: 3), (Offset: 60000d, Count: 1) },
            translations["buckets"]!.AsArray().Select(bucket =>
                (Offset: bucket!["offsetMs"]!.GetValue<double>(), Count: bucket["count"]!.GetValue<int>())));
        var ordinaryBucket = Assert.Single(initial["buckets"]!.AsArray());
        Assert.Equal(4, ordinaryBucket!["danmuCount"]!.GetValue<int>());
        Assert.Equal(5, ordinaryBucket["activeUsers"]!.GetValue<int>());
        var ordinaryUsers = initial["topUsers"]!.AsArray();
        Assert.DoesNotContain(ordinaryUsers, user => user!["userId"]!.GetValue<string>() == "201");
        var mixedUser = Assert.Single(ordinaryUsers, user => user!["userId"]!.GetValue<string>() == "202");
        Assert.Equal(1, mixedUser!["messages"]!.GetValue<int>());
        Assert.Equal(1, mixedUser["danmuCount"]!.GetValue<int>());
        var payingUser = Assert.Single(ordinaryUsers, user => user!["userId"]!.GetValue<string>() == "203");
        Assert.Equal(0, payingUser!["danmuCount"]!.GetValue<int>());
        Assert.Equal(32m, payingUser["paidAmount"]!.GetValue<decimal>());
        Assert.DoesNotContain(initial["userRankings"]!["danmuCount"]!.AsArray(),
            user => user!["userId"]!.GetValue<string>() is "201" or "203");

        var account = await fixture.CreateUserAsync("analytics-translator@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS);
        await SetPersonalPatternAsync(viewer, "^EN:");
        var personal = await ReportAsync(viewer, session);
        Assert.Equal(7, personal["danmuCount"]!.GetValue<int>());
        Assert.Equal(new[] { "204" }, TranslationUsers(personal));
        Assert.Equal(4, (await ReportAsync(fixture.Admin, session))["translations"]!["count"]!.GetValue<int>());

        await SetGlobalPatternAsync(fixture, "^JP:");
        var global = await ReportAsync(fixture.Admin, session);
        Assert.Equal(7, global["danmuCount"]!.GetValue<int>());
        Assert.Equal(new[] { "205" }, TranslationUsers(global));
        Assert.Equal(new[] { "204" }, TranslationUsers(await ReportAsync(viewer, session)));
        Assert.Equal(new[] { "205" }, TranslationUsers(await ReportAsync(fixture.Admin, session)));

        await SetPersonalPatternAsync(viewer, "^(?:EN|JP):");
        var updated = await ReportAsync(viewer, session);
        Assert.Equal(6, updated["danmuCount"]!.GetValue<int>());
        Assert.Equal(2, updated["translations"]!["count"]!.GetValue<int>());
        Assert.Equal(new[] { "204", "205" }, TranslationUsers(updated).Order(StringComparer.Ordinal));
        await SetPersonalPatternAsync(viewer, null);
        Assert.Equal(new[] { "205" }, TranslationUsers(await ReportAsync(viewer, session)));
        await SetGlobalPatternAsync(fixture, "^(?:EN|JP):");
        Assert.Equal(2, (await ReportAsync(viewer, session))["translations"]!["count"]!.GetValue<int>());
        Assert.Equal(2, (await ReportAsync(fixture.Admin, session))["translations"]!["count"]!.GetValue<int>());
    }

    /// <summary>统计要求观看和分析权限，昂贵规则不能返回缓存旧结果，超时按请求语言报错且可修改后恢复。</summary>
    [MongoHttpFact]
    public async Task AnalyticsRequireBothPermissionsAndLocalizeRegexTimeoutsWithoutPoisoningCache()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var session = await SeedSessionAsync(fixture, Danmu("costly", "301", new string('a', 100000) + "!", 1000));
        await SetGlobalPatternAsync(fixture, "^译：");
        var path = $"/api/sessions/{session.Id}/analytics";
        var anonymous = fixture.NewBrowser();
        using (var denied = await anonymous.Client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        var account = await fixture.CreateUserAsync("analytics-permissions@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        using (var denied = await viewer.Client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_ANALYTICS);
        using (var denied = await viewer.Client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS);
        var initial = await ReportAsync(viewer, session);
        Assert.Equal(1, initial["danmuCount"]!.GetValue<int>());
        Assert.Equal(0, initial["translations"]!["count"]!.GetValue<int>());
        await SetPersonalPatternAsync(viewer, "(a+)+$");
        foreach (var (language, detail) in new[]
                 {
                     ("zh-CN", "同传正则匹配超时，请简化表达式后重试。"),
                     ("en-US", "The translation regular expression timed out. Simplify the pattern and try again."),
                     ("ja-JP", "同時通訳の正規表現の照合がタイムアウトしました。式を簡略化して再試行してください。")
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.AcceptLanguage.ParseAdd(language);
            using var timeout = await viewer.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, timeout.StatusCode);
            var problem = (await timeout.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("TRANSLATION_DANMU_PATTERN_TIMEOUT", problem["code"]!.GetValue<string>());
            Assert.Equal(detail, problem["detail"]!.GetValue<string>());
            Assert.Equal(language, Assert.Single(timeout.Content.Headers.ContentLanguage));
        }
        await SetPersonalPatternAsync(viewer, "^译：");
        var recovered = await ReportAsync(viewer, session);
        Assert.Equal(1, recovered["danmuCount"]!.GetValue<int>());
        Assert.Equal(0, recovered["translations"]!["count"]!.GetValue<int>());
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS);
        using var revoked = await viewer.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    private static async Task<BroadcastSession> SeedSessionAsync(HttpServerFixture fixture, params RecordedEventEnvelope[] items)
    {
        var session = new BroadcastSession
        {
            RoomId = 100,
            Title = "同传统计回归",
            DirectoryPath = "analytics-translations-" + Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            EndedAtUtc = DateTime.UtcNow,
            State = "Completed",
            HasDanmu = true
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id,
            RelativePath = "recording.flv",
            DurationMs = 180000,
            CommittedBytes = 64,
            IsClosed = true
        });
        await using var writer = new SessionEventWriter(Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕"));
        foreach (var item in items)
        {
            await writer.WriteAsync(item);
        }
        return session;
    }

    private static RecordedEventEnvelope Danmu(string eventId, string userId, string text, double offsetMs) => new()
    {
        EventId = eventId,
        UserId = userId,
        UserName = "用户" + userId,
        Text = text,
        OffsetMs = offsetMs
    };

    private static async Task<JsonObject> ReportAsync(HttpBrowser browser, BroadcastSession session) =>
        await browser.Client.GetFromJsonAsync<JsonObject>($"/api/sessions/{session.Id}/analytics")
        ?? throw new InvalidDataException("Missing analytics response.");

    private static IEnumerable<string> TranslationUsers(JsonObject report) =>
        report["translations"]!["topUsers"]!.AsArray().Select(user => user!["userId"]!.GetValue<string>());

    private static async Task SetGlobalPatternAsync(HttpServerFixture fixture, string pattern)
    {
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["translationDanmuPattern"] = pattern;
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SetPersonalPatternAsync(HttpBrowser browser, string? pattern)
    {
        using var response = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SetPermissionsAsync(HttpServerFixture fixture, string userId, params string[] permissions)
    {
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/permissions", new PermissionSet(permissions));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
