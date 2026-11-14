using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证录像共享配置的独立权限、版本、CSRF 和归组冲突边界。</summary>
public sealed class SessionConfigurationHttpTests
{
    /// <summary>观看者可读取共享配置，只有单独授权的配置者可更改，旧版本不能覆盖新值。</summary>
    [MongoHttpFact]
    public async Task ConfigurationRequiresIndependentPermissionAndPersistsAcrossRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var session = await SeedAsync(fixture, 0);
        var path = $"/api/sessions/{session.Id}/configuration";
        var account = await fixture.CreateUserAsync("recording-config@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        var initial = (await viewer.Client.GetFromJsonAsync<SessionConfigurationResponse>(path))!;
        Assert.Null(initial.TranslationDanmuPatternOverride);
        Assert.Equal(1, initial.Revision);
        await PermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.MANAGE_RECORDINGS);
        using (var denied = await viewer.SendAsync(HttpMethod.Put, path, new SessionConfigurationRequest("^EN:", 1)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await PermissionsAsync(fixture, account.Id, PermissionIds.CONFIGURE_RECORDINGS);
        using (var denied = await viewer.SendAsync(HttpMethod.Put, path, new SessionConfigurationRequest("^EN:", 1)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await PermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.CONFIGURE_RECORDINGS);
        using (var csrf = await viewer.SendAsync(HttpMethod.Put, path, new SessionConfigurationRequest("^EN:", 1), includeCsrf: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        }
        using (var invalid = await viewer.SendAsync(HttpMethod.Put, path, new SessionConfigurationRequest("[", 1)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var saved = await viewer.SendAsync(HttpMethod.Put, path, new SessionConfigurationRequest("^EN:", 1)))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(new("^EN:", "^EN:", 2), await saved.Content.ReadFromJsonAsync<SessionConfigurationResponse>());
        }
        using (var stale = await fixture.Admin.SendAsync(HttpMethod.Put, path, new SessionConfigurationRequest(null, 1)))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("SESSION_CONFIGURATION_CHANGED", (await stale.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
        await fixture.RestartAsync();
        var restored = fixture.NewBrowser();
        await restored.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Equal(new("^EN:", "^EN:", 2), await restored.Client.GetFromJsonAsync<SessionConfigurationResponse>(path));
        using var inherit = await restored.SendAsync(HttpMethod.Put, path, new SessionConfigurationRequest(null, 2));
        Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
        Assert.Null((await inherit.Content.ReadFromJsonAsync<SessionConfigurationResponse>())!.TranslationDanmuPatternOverride);
    }

    /// <summary>归组冲突必须由同时具有管理和配置权限的用户显式选择；配置权限不授予归组能力。</summary>
    [MongoHttpFact]
    public async Task GroupingConflictsRequireBothPermissionsAndExplicitResolution()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var first = await SeedAsync(fixture, 0);
        var second = await SeedAsync(fixture, 3000);
        using (var configuration = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/sessions/{second.Id}/configuration", new SessionConfigurationRequest("^EN:", 1)))
        {
            Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        }
        var account = await fixture.CreateUserAsync("group-config@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        var path = $"/api/sessions/{first.Id}/group";
        var choice = new { sessionIds = new[] { second.Id }, configuration = new SessionGroupConfiguration(null) };
        await PermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.MANAGE_RECORDINGS);
        using (var conflict = await viewer.SendAsync(HttpMethod.Post, path, new { sessionIds = new[] { second.Id } }))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("GROUPING_CONFIGURATION_REQUIRED", (await conflict.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
        using (var denied = await viewer.SendAsync(HttpMethod.Post, path, choice))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await PermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.CONFIGURE_RECORDINGS);
        using (var denied = await viewer.SendAsync(HttpMethod.Post, path, choice))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await PermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.MANAGE_RECORDINGS, PermissionIds.CONFIGURE_RECORDINGS);
        using var grouped = await viewer.SendAsync(HttpMethod.Post, path, choice);
        Assert.Equal(HttpStatusCode.OK, grouped.StatusCode);
        Assert.Null((await grouped.Content.ReadFromJsonAsync<JsonObject>())!["translationDanmuPatternOverride"]);
    }

    private static async Task<BroadcastSession> SeedAsync(HttpServerFixture fixture, double offsetMs)
    {
        var origin = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var session = new BroadcastSession
        {
            StreamerId = "configuration-streamer",
            DirectoryPath = "configuration-" + Guid.NewGuid().ToString("N"),
            Title = "录像配置",
            StartedAtUtc = origin.AddMilliseconds(offsetMs),
            EndedAtUtc = origin.AddMilliseconds(offsetMs + 1000),
            State = "Completed"
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        Directory.CreateDirectory(Path.Combine(fixture.StoragePath, session.DirectoryPath));
        await File.WriteAllTextAsync(Path.Combine(fixture.StoragePath, session.DirectoryPath, "recording.flv"), "recording");
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id, RelativePath = "recording.flv", DurationMs = 1000, CommittedBytes = 9, IsClosed = true
        });
        return session;
    }

    private static async Task PermissionsAsync(HttpServerFixture fixture, string userId, params string[] permissions)
    {
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/permissions", new PermissionSet(permissions));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
