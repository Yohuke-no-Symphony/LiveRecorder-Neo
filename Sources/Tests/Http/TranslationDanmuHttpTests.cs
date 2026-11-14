using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证全局同传规则、录像覆盖及全场弹幕筛选接口。</summary>
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

    /// <summary>旧个人设置入口被移除，登录响应不再暴露个人同传规则。</summary>
    [MongoHttpFact]
    public async Task PersonalPatternEndpointAndProfileFieldAreRemoved()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var user = await fixture.CreateUserAsync("translation-user@example.test");
        var browser = fixture.NewBrowser();
        var login = await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.False(login["user"]!.AsObject().ContainsKey("translationDanmuPattern"));
        using var update = await browser.SendAsync(HttpMethod.Put, "/api/auth/translation-danmu-pattern", new { pattern = "^EN:" });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    /// <summary>同传分页覆盖整场、排除其他事件及播放尾部，所有账号使用相同的录像优先规则。</summary>
    [MongoHttpFact]
    public async Task QueryFiltersBeforePagingAndUsesSharedRecordingConfiguration()
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
            await writer.WriteAsync(new() { OffsetMs = 3000, Text = "EN: recording translation" });
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
        using var recording = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/sessions/{session.Id}/configuration",
            new { translationDanmuPatternOverride = "^EN:", expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, recording.StatusCode);
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["translationDanmuPattern"] = "^JP:";
        using var global = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);

        var viewerPage = (await browser.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true"))!;
        Assert.Equal("EN: recording translation", Assert.Single(viewerPage.Items).Text);
        var adminPage = (await fixture.Admin.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true"))!;
        Assert.Equal("EN: recording translation", Assert.Single(adminPage.Items).Text);
        using var inherit = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/sessions/{session.Id}/configuration",
            new { translationDanmuPatternOverride = (string?)null, expectedRevision = 2 });
        Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
        var inherited = (await browser.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?translationsOnly=true"))!;
        Assert.Equal("JP: global translation", Assert.Single(inherited.Items).Text);

        var unfiltered = (await browser.Client.GetFromJsonAsync<EventPage>($"/api/sessions/{session.Id}/events?fromMs=0&toMs=10000&limit=2"))!;
        Assert.All(unfiltered.Items, item => Assert.Equal("普通弹幕", item.Text));
        using var costly = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/sessions/{session.Id}/configuration",
            new { translationDanmuPatternOverride = "(a+)+$", expectedRevision = 3 });
        Assert.Equal(HttpStatusCode.OK, costly.StatusCode);
        using var timeout = await browser.Client.GetAsync($"/api/sessions/{session.Id}/events?translationsOnly=true");
        Assert.Equal(HttpStatusCode.BadRequest, timeout.StatusCode);
        var timeoutError = await timeout.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("TRANSLATION_DANMU_PATTERN_TIMEOUT", timeoutError!["code"]!.GetValue<string>());
    }
}
