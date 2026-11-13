using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证断流结束时长的设置接口、边界和重启持久化。</summary>
public sealed class RecordingSettingsHttpTests
{
    [MongoHttpFact]
    public async Task AdministratorCanPersistStreamDisconnectTimeoutAndReloadAfterRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(5, settings!["streamDisconnectTimeoutMinutes"]!.GetValue<int>());

        foreach (var minutes in new[] { 1, 1440, 17 })
        {
            settings["streamDisconnectTimeoutMinutes"] = minutes;
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(minutes, saved!["streamDisconnectTimeoutMinutes"]!.GetValue<int>());
        }

        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var restored = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(17, restored!["streamDisconnectTimeoutMinutes"]!.GetValue<int>());
    }

    [MongoHttpFact]
    public async Task InvalidStreamDisconnectTimeoutCannotReplaceSavedConfiguration()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var original = await File.ReadAllTextAsync(fixture.ConfigFile);

        foreach (var minutes in new[] { -1, 0, 1441 })
        {
            settings!["streamDisconnectTimeoutMinutes"] = minutes;
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("SETTINGS_INVALID", error!["code"]!.GetValue<string>());
            Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
        }

        var saved = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(5, saved!["streamDisconnectTimeoutMinutes"]!.GetValue<int>());
    }
}
