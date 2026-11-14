using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证真实 Cookie、CSRF 与后台权限变化。</summary>
public sealed class AuthenticationHttpTests
{
    [MongoHttpFact]
    public async Task SetupLoginAndSettingsUseFreshIdentityBoundCsrfAndRedactSecrets()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var anonymousToken = fixture.Admin.CsrfToken;
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.NotEqual(anonymousToken, fixture.Admin.CsrfToken);
        var paths = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings/paths");
        Assert.Equal(fixture.DataDirectory, paths!["dataDirectory"]!.GetValue<string>());
        Assert.Equal(fixture.StoragePath, paths["recordsDirectory"]!.GetValue<string>());
        Assert.Equal(fixture.CachePath, paths["cacheDirectory"]!.GetValue<string>());
        var settings = await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options) ?? throw new InvalidDataException("Missing settings.");
        settings.RegistrationEnabled = true;
        settings.Smtp.Password = "test-smtp-secret";
        settings.OAuthProviders.Add(new() { Id = "test-provider", Name = "Test", ClientSecret = "test-oauth-secret" });
        using var update = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var body = await update.Content.ReadAsStringAsync();
        Assert.DoesNotContain("test-smtp-secret", body);
        Assert.DoesNotContain("test-oauth-secret", body);
        var saved = await update.Content.ReadFromJsonAsync<ServerSettings>(JsonFiles.Options) ?? throw new InvalidDataException("Missing saved settings.");
        Assert.True(saved.RegistrationEnabled);
        Assert.Equal("", saved.Smtp.Password);
        Assert.Equal("", Assert.Single(saved.OAuthProviders).ClientSecret);
        using var preserve = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", saved);
        Assert.Equal(HttpStatusCode.OK, preserve.StatusCode);
        var protector = fixture.App.Services.GetRequiredService<ISecretProtector>();
        var stored = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal("test-smtp-secret", protector.Unprotect(stored.Smtp.Password));
        Assert.Equal("test-oauth-secret", protector.Unprotect(Assert.Single(stored.OAuthProviders).ClientSecret));
        var settingsFile = fixture.ConfigFile;
        var persisted = await File.ReadAllTextAsync(settingsFile);
        Assert.DoesNotContain("test-smtp-secret", persisted);
        Assert.DoesNotContain("test-oauth-secret", persisted);
        Assert.False(await fixture.Context.Collection<ServerSettings>("settings").Find(x => x.Id == "server").AnyAsync());
        using var missingCsrf = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", saved, includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
    }

    [MongoHttpFact]
    public async Task AnonymousCannotReadMediaEventsDownloadsTasksOrManagement()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var anonymous = fixture.NewBrowser();
        foreach (var path in new[]
        {
            "/api/streamers", "/api/sessions", "/api/sessions/example", "/api/sessions/example/cover",
            "/api/sessions/example/events", "/api/sessions/example/analytics", "/api/streamers/example/avatar",
            "/api/playback/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/index.m3u8",
            "/api/playback/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/segment0.ts",
            "/api/jobs", "/api/jobs/example", "/api/jobs/example/outputs/0", "/api/clips", "/api/clips/example/download", "/api/clips/example/media", "/api/auth/identities",
            "/api/trash", "/api/storage", "/api/settings", "/api/settings/paths", "/api/admin/users", "/api/admin/bilibili/accounts"
        })
        {
            using var response = await anonymous.Client.GetAsync(path);
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{path} returned {(int)response.StatusCode}.");
        }
        await anonymous.RefreshCsrfAsync();
        using var task = await anonymous.SendAsync(HttpMethod.Post, "/api/jobs", new { kind = "Clip", sessionId = "example", title = "example", startMs = 0, endMs = 1000 });
        Assert.Equal(HttpStatusCode.Unauthorized, task.StatusCode);
        using var hub = await anonymous.Client.PostAsync("/hubs/status/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);
    }

    [MongoHttpFact]
    public async Task PendingUserCannotObtainAuthenticatedSession()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var pending = await fixture.CreateUserAsync("pending@example.test", "Pending");
        var browser = fixture.NewBrowser();
        await browser.RefreshCsrfAsync();
        using var login = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = pending.Email, password = HttpServerFixture.USER_PASSWORD });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        using var library = await browser.Client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, library.StatusCode);
    }

    [MongoHttpFact]
    public async Task ExistingCookieLosesAdminRoleAndDisabledCookieCannotReadOrMutate()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var user = await fixture.CreateUserAsync("secondary-admin@example.test", role: "Admin");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using (var settings = await browser.Client.GetAsync("/api/settings"))
        {
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        }
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(x => x.Id == user.Id, Builders<UserAccount>.Update.Set(x => x.Role, "User"));
        using (var settings = await browser.Client.GetAsync("/api/settings"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        }
        var me = await browser.RefreshCsrfAsync();
        Assert.Equal("User", me["user"]!["role"]!.GetValue<string>());
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(x => x.Id == user.Id, Builders<UserAccount>.Update.Set(x => x.Status, "Disabled"));
        using var forbiddenWrite = await browser.SendAsync(HttpMethod.Post, "/api/jobs", new { kind = "Clip", sessionId = "example", title = "example", startMs = 0, endMs = 1000 });
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenWrite.StatusCode);
        using var forbiddenRead = await browser.Client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenRead.StatusCode);
    }

    [MongoHttpFact]
    public async Task UnknownApiReturnsProblem404InsteadOfHtmlFallback()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/not-a-real-endpoint");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await fixture.Admin.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(404, problem!["status"]!.GetValue<int>());
    }
}
