using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证公开界面配置、账户语言与管理员默认语言的隔离和持久化。</summary>
public sealed class LanguagePreferenceHttpTests
{
    [Fact]
    public async Task UninstalledServerPublishesOnlyChineseUiDefaultWithoutDatabase()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/ui-settings");
        Assert.Equal("defaultLanguage", Assert.Single(settings!).Key);
        Assert.Equal("zh-CN", settings!["defaultLanguage"]!.GetValue<string>());
        using var response = await fixture.Admin.Client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [MongoHttpFact]
    public async Task AccountLanguageIsPrivateValidatedAndSurvivesNewSessionsAndRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var user = await fixture.CreateUserAsync("language@example.test");
        var browser = fixture.NewBrowser();
        var login = await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Null(login["user"]!["language"]);
        foreach (var language in new[] { "en-US", "ja-JP", "zh-CN" })
        {
            using var update = await browser.SendAsync(HttpMethod.Put, "/api/auth/language", new { language });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var profile = await update.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(user.Id, profile!["id"]!.GetValue<string>());
            Assert.Equal(language, profile["language"]!.GetValue<string>());
        }
        using var missingCsrf = await browser.SendAsync(HttpMethod.Put, "/api/auth/language", new { language = "ja-JP" }, includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var invalid = await browser.SendAsync(HttpMethod.Put, "/api/auth/language", new { language = "fr-FR" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var stored = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Id == user.Id).SingleAsync();
        Assert.Equal("User", stored.Role);
        var me = await browser.RefreshCsrfAsync();
        Assert.Equal("zh-CN", me["user"]!["language"]!.GetValue<string>());
        var other = fixture.NewBrowser();
        var otherLogin = await other.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Equal("zh-CN", otherLogin["user"]!["language"]!.GetValue<string>());
        await fixture.RestartAsync();
        var restarted = fixture.NewBrowser();
        var restored = await restarted.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Equal("zh-CN", restored["user"]!["language"]!.GetValue<string>());
        using var inherit = await restarted.SendAsync(HttpMethod.Put, "/api/auth/language", new { language = (string?)null });
        Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
        var inherited = await restarted.RefreshCsrfAsync();
        Assert.Null(inherited["user"]!["language"]);
        var anonymous = fixture.NewBrowser();
        await anonymous.RefreshCsrfAsync();
        using var denied = await anonymous.SendAsync(HttpMethod.Put, "/api/auth/language", new { language = "en-US" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [MongoHttpFact]
    public async Task OnlyAdministratorCanPersistGlobalDefaultAndPublicResponseDoesNotExposeSettings()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal("zh-CN", settings!["defaultLanguage"]!.GetValue<string>());
        settings["defaultLanguage"] = "ja-JP";
        using var update = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var user = await fixture.CreateUserAsync("default-language@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var denied = await viewer.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        settings["defaultLanguage"] = "invalid";
        using var invalid = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var anonymous = fixture.NewBrowser();
        var published = await anonymous.Client.GetFromJsonAsync<JsonObject>("/api/ui-settings");
        Assert.Equal("defaultLanguage", Assert.Single(published!).Key);
        Assert.Equal("ja-JP", published!["defaultLanguage"]!.GetValue<string>());
        await fixture.RestartAsync();
        var restarted = fixture.NewBrowser();
        var restored = await restarted.Client.GetFromJsonAsync<JsonObject>("/api/ui-settings");
        Assert.Equal("ja-JP", restored!["defaultLanguage"]!.GetValue<string>());
    }
}
