using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证任务日志保留时间的设置边界、权限、兼容请求和重启持久化。</summary>
public sealed class TaskLogRetentionSettingsHttpTests
{
    /// <summary>默认保留七天，永久保留和有限保留的合法边界均可跨重启恢复。</summary>
    [MongoHttpFact]
    public async Task AdministratorPersistsTaskLogRetentionBoundariesAcrossRestart()
    {
        await using var fixture = await FixtureAsync();
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(7, settings!["taskLogRetentionDays"]!.GetValue<int>());

        foreach (var days in new[] { 0, 1, 3650, 30 })
        {
            settings["taskLogRetentionDays"] = days;
            using (var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var saved = await response.Content.ReadFromJsonAsync<JsonObject>();
                Assert.Equal(days, saved!["taskLogRetentionDays"]!.GetValue<int>());
            }

            await fixture.RestartAsync();
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
            Assert.Equal(days, settings!["taskLogRetentionDays"]!.GetValue<int>());
        }
    }

    /// <summary>负数、小数和超出上限的保留天数不能写入设置，也不能夹带修改其他配置。</summary>
    [MongoHttpFact]
    public async Task InvalidTaskLogRetentionCannotReplaceSavedConfiguration()
    {
        await using var fixture = await FixtureAsync();
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        settings!["taskLogRetentionDays"] = 30;
        using (var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var original = await File.ReadAllTextAsync(fixture.ConfigFile);
        var originalQuality = settings["quality"]!.GetValue<int>();
        settings["quality"] = originalQuality + 1;
        JsonNode[] invalid = [JsonValue.Create(-1)!, JsonValue.Create(1.5)!, JsonValue.Create(3651)!];

        foreach (var value in invalid)
        {
            settings["taskLogRetentionDays"] = value;
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
        }

        var saved = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(30, saved!["taskLogRetentionDays"]!.GetValue<int>());
        Assert.Equal(originalQuality, saved["quality"]!.GetValue<int>());
    }

    /// <summary>普通用户即使发送合法的完整设置和防伪令牌，也不能修改管理员保留策略。</summary>
    [MongoHttpFact]
    public async Task NonAdministratorCannotChangeTaskLogRetention()
    {
        await using var fixture = await FixtureAsync();
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var original = await File.ReadAllTextAsync(fixture.ConfigFile);
        settings!["taskLogRetentionDays"] = 0;
        var user = await fixture.CreateUserAsync("task-log-retention-member@example.test");
        var member = fixture.NewBrowser();
        await member.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);

        using var response = await member.SendAsync(HttpMethod.Put, "/api/settings", settings);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
        var saved = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(7, saved!["taskLogRetentionDays"]!.GetValue<int>());
    }

    /// <summary>旧客户端省略新增字段时可更新其他设置，并保留管理员已经保存的保留天数。</summary>
    [MongoHttpFact]
    public async Task LegacySettingsRequestsPreserveTheSavedTaskLogRetention()
    {
        await using var fixture = await FixtureAsync();
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        var legacy = settings!.DeepClone().AsObject();
        Assert.True(legacy.Remove("taskLogRetentionDays"));
        legacy["quality"] = 400;
        settings["taskLogRetentionDays"] = 30;
        using (var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", legacy))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(30, saved!["taskLogRetentionDays"]!.GetValue<int>());
            Assert.Equal(400, saved["quality"]!.GetValue<int>());
        }

        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var restored = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(30, restored!["taskLogRetentionDays"]!.GetValue<int>());
        Assert.Equal(400, restored["quality"]!.GetValue<int>());
    }

    private static async Task<HttpServerFixture> FixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }
}
