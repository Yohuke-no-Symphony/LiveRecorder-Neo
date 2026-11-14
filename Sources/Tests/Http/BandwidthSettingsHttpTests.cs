using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证带宽设置的管理员权限、输入边界和重启持久化。</summary>
public sealed class BandwidthSettingsHttpTests
{
    /// <summary>管理员可保存并恢复带宽设置，普通用户不能读取或修改全局设置。</summary>
    [MongoHttpFact]
    public async Task AdministratorPersistsBandwidthSettingsAcrossRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var defaults = settings!["bandwidth"]!.AsObject();
        Assert.Equal(0, defaults["userDownloadLimitBytesPerSecond"]!.GetValue<long>());
        Assert.False(defaults["smartLimitEnabled"]!.GetValue<bool>());
        Assert.Equal(0, defaults["totalBytesPerSecond"]!.GetValue<long>());
        Assert.Equal(10, defaults["downloadGuaranteedPercent"]!.GetValue<int>());
        var requested = Configuration(512 * 1024, true, 10 * 1024 * 1024, 25);
        settings["bandwidth"] = requested.DeepClone();
        using (var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.True(JsonNode.DeepEquals(requested, saved!["bandwidth"]));
        }

        var user = await fixture.CreateUserAsync("bandwidth-reader@example.test");
        var member = fixture.NewBrowser();
        await member.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using (var response = await member.Client.GetAsync("/api/settings"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var response = await member.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var restored = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.True(JsonNode.DeepEquals(requested, restored!["bandwidth"]));
    }

    /// <summary>空配置、非法速率和保证比例不能替换已保存的带宽设置。</summary>
    [MongoHttpFact]
    public async Task InvalidBandwidthConfigurationPreservesSavedSettings()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var original = await File.ReadAllTextAsync(fixture.ConfigFile);
        var originalBandwidth = settings!["bandwidth"]!.DeepClone();
        JsonNode?[] invalid =
        [
            null,
            Configuration(-1, false, 0, 10),
            Configuration(0, false, -1, 10),
            Configuration(9007199254740992, false, 0, 10),
            Configuration(0, false, 9007199254740992, 10),
            Configuration(0, true, 0, 10),
            Configuration(0, true, 1024, -1),
            Configuration(0, true, 1024, 101)
        ];
        foreach (var value in invalid)
        {
            settings["bandwidth"] = value;
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
        }
        var saved = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.True(JsonNode.DeepEquals(originalBandwidth, saved!["bandwidth"]));
    }

    /// <summary>关闭智能模式时可保留总带宽，合法极值与缺省字段可往返保存。</summary>
    [MongoHttpFact]
    public async Task ValidBoundariesAndLegacyRequestsRoundTrip()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        foreach (var value in new[] { Configuration(0, true, 1, 0), Configuration(9007199254740991, false, 9007199254740991, 100) })
        {
            settings!["bandwidth"] = value;
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.True(JsonNode.DeepEquals(value, saved!["bandwidth"]));
        }
        settings!.Remove("bandwidth");
        using var legacy = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        var restored = await legacy.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(JsonNode.DeepEquals(Configuration(0, false, 0, 10), restored!["bandwidth"]));
    }

    private static JsonObject Configuration(long userLimit, bool smart, long total, int guarantee) => new()
    {
        ["userDownloadLimitBytesPerSecond"] = userLimit,
        ["smartLimitEnabled"] = smart,
        ["totalBytesPerSecond"] = total,
        ["downloadGuaranteedPercent"] = guarantee
    };
}
