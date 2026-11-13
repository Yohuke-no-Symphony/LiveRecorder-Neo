using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证全局同传规则、账号覆盖及全场弹幕筛选接口。</summary>
public sealed class TranslationDanmuHttpTests
{
    /// <summary>全局规则只允许管理员修改，非法规则不得替换已保存配置。</summary>
    [MongoHttpFact]
    public async Task GlobalPatternIsValidatedAndPersistsAcrossRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        Assert.Equal(".*?【.*?】", settings["translationDanmuPattern"]?.GetValue<string>());
        settings["translationDanmuPattern"] = "^翻译：";
        using var updated = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var original = await File.ReadAllTextAsync(fixture.ConfigFile);

        foreach (var pattern in new[] { null, "", " ", "[", new string('a', 2049) })
        {
            settings["translationDanmuPattern"] = pattern;
            using var invalid = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var error = await invalid.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("TRANSLATION_DANMU_PATTERN_INVALID", error!["code"]!.GetValue<string>());
            Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
        }

        var user = await fixture.CreateUserAsync("translation-settings@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var denied = await viewer.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var restored = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal("^翻译：", restored!["translationDanmuPattern"]!.GetValue<string>());
    }

    /// <summary>个人规则绑定当前账号，跨会话持久化，并允许清除后继承全局规则。</summary>
    [MongoHttpFact]
    public async Task PersonalPatternIsIsolatedValidatedAndCanInheritGlobalDefault()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var user = await fixture.CreateUserAsync("translation-user@example.test");
        var otherUser = await fixture.CreateUserAsync("translation-other@example.test");
        var browser = fixture.NewBrowser();
        var login = await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Null(login["user"]!["translationDanmuPattern"]);

        using var update = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = "^EN:", userId = otherUser.Id });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var profile = await update.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(user.Id, profile!["id"]!.GetValue<string>());
        Assert.Equal("^EN:", profile["translationDanmuPattern"]!.GetValue<string>());

        using var missingCsrf = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = "^JP:" }, includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        foreach (var pattern in new[] { "", " ", "[", new string('a', 2049) })
        {
            using var invalid = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        var other = fixture.NewBrowser();
        var otherLogin = await other.LoginAsync(otherUser.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Null(otherLogin["user"]!["translationDanmuPattern"]);
        var secondBrowser = fixture.NewBrowser();
        var secondLogin = await secondBrowser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Equal("^EN:", secondLogin["user"]!["translationDanmuPattern"]!.GetValue<string>());

        await fixture.RestartAsync();
        var restarted = fixture.NewBrowser();
        var restored = await restarted.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Equal("^EN:", restored["user"]!["translationDanmuPattern"]!.GetValue<string>());
        using var inherit = await restarted.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = (string?)null });
        Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
        Assert.Null((await restarted.RefreshCsrfAsync())["user"]!["translationDanmuPattern"]);

        var anonymous = fixture.NewBrowser();
        await anonymous.RefreshCsrfAsync();
        using var anonymousUpdate = await anonymous.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = "^EN:" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousUpdate.StatusCode);
    }

    /// <summary>同传分页覆盖整场、排除其他事件及播放尾部，并按个人优先规则查询。</summary>
    [MongoHttpFact]
    public async Task QueryFiltersBeforePagingAndUsesTheCurrentAccountsEffectivePattern()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var session = new BroadcastSession
        {
            Title = "同传查询", DirectoryPath = "translation-events", HasDanmu = true,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5), EndedAtUtc = DateTime.UtcNow, State = "Completed"
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id, RelativePath = "recording.flv", DurationMs = 180000, CommittedBytes = 64, IsClosed = true
        });
        await using (var writer = new SessionEventWriter(Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕")))
        {
            for (var index = 0; index < 500; index++)
            {
                await writer.WriteAsync(new() { OffsetMs = index, Text = "普通弹幕" });
            }
            await writer.WriteAsync(new() { OffsetMs = 1000, Text = "译者【第一句】后缀" });
            await writer.WriteAsync(new() { OffsetMs = 2000, Kind = "SuperChat", Text = "【付费留言】" });
            await writer.WriteAsync(new() { OffsetMs = 120000, Text = "【两分钟之后】" });
            await writer.WriteAsync(new() { OffsetMs = 500, Text = "【迟到补录】" });
            await writer.WriteAsync(new() { OffsetMs = 3000, Text = "EN: personal translation" });
            await writer.WriteAsync(new() { OffsetMs = 4000, Text = "JP: global translation" });
            await writer.WriteAsync(new() { OffsetMs = 180000, Text = "【播放终点之外】" });
            await writer.WriteAsync(new() { OffsetMs = 5000, Text = new string('a', 100000) + "!" });
        }

        var first = (await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true&limit=2"))!;
        Assert.Equal(new[] { "译者【第一句】后缀", "【两分钟之后】" }, first.Items.Select(item => item.Text));
        Assert.NotNull(first.NextCursor);
        var second = (await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true&limit=2&cursor={first.NextCursor}"))!;
        Assert.Equal("【迟到补录】", Assert.Single(second.Items).Text);
        Assert.Null(second.NextCursor);

        var user = await fixture.CreateUserAsync("translation-viewer@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var personal = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = "^EN:" });
        Assert.Equal(HttpStatusCode.OK, personal.StatusCode);
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["translationDanmuPattern"] = "^JP:";
        using var global = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);

        var viewerPage = (await browser.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true"))!;
        Assert.Equal("EN: personal translation", Assert.Single(viewerPage.Items).Text);
        var adminPage = (await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true"))!;
        Assert.Equal("JP: global translation", Assert.Single(adminPage.Items).Text);
        using var inherit = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = (string?)null });
        Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
        var inherited = (await browser.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true"))!;
        Assert.Equal("JP: global translation", Assert.Single(inherited.Items).Text);

        var unfiltered = (await browser.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?fromMs=0&toMs=10000&limit=2"))!;
        Assert.All(unfiltered.Items, item => Assert.Equal("普通弹幕", item.Text));
        using var costly = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = "(a+)+$" });
        Assert.Equal(HttpStatusCode.OK, costly.StatusCode);
        using var timeout = await browser.Client.GetAsync($"/api/sessions/{session.Id}/events?translationsOnly=true");
        Assert.Equal(HttpStatusCode.BadRequest, timeout.StatusCode);
        var timeoutError = await timeout.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("TRANSLATION_DANMU_PATTERN_TIMEOUT", timeoutError!["code"]!.GetValue<string>());
    }
}
