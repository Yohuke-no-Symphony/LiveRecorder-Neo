using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Server.Logging;

namespace Yohuke.LiveRecorder.Http.Tests;

public sealed class ServerLogHttpTests
{
    private const string LOGS_PATH = "/api/admin/server-logs";

    [MongoHttpFact]
    public async Task LogEndpointRevalidatesAdministratorCookiesAndDoesNotCacheLogs()
    {
        await using var fixture = await HttpServerFixture.CreateAsync("Production", "--Logging:LogLevel:Default", "Information",
            "--Logging:LogLevel:Yohuke.LiveRecorder.SuppressedCategory", "Error");
        var installationToken = fixture.App.Services.GetRequiredService<IBootstrapStore>().InstallationToken;
        await fixture.InstallAsync();
        var anonymous = fixture.NewBrowser();
        using var unauthenticated = await anonymous.Client.GetAsync(LOGS_PATH);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var viewer = await fixture.CreateUserAsync("server-log-viewer@example.test");
        var viewerBrowser = fixture.NewBrowser();
        await viewerBrowser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);
        using var forbidden = await viewerBrowser.Client.GetAsync(LOGS_PATH);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var admin = await fixture.CreateUserAsync("server-log-admin@example.test", role: "Admin");
        var adminBrowser = fixture.NewBrowser();
        await adminBrowser.LoginAsync(admin.Email, HttpServerFixture.USER_PASSWORD);
        fixture.App.Logger.LogWarning("Application logger is captured");
        var factory = fixture.App.Services.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("Yohuke.LiveRecorder.ServerLogTest");
        var suppressed = factory.CreateLogger("Yohuke.LiveRecorder.SuppressedCategory");
        suppressed.LogWarning("Filtered warning");
        suppressed.LogError("Error above configured minimum");
        logger.LogWarning("Recording {SessionId} failed with credentials {Authorization}", "recording-01", "secret-value");

        using var response = await adminBrowser.Client.GetAsync(LOGS_PATH);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(installationToken, json);
        Assert.DoesNotContain("secret-value", json);
        var page = await response.Content.ReadFromJsonAsync<ServerLogPage>();
        Assert.NotNull(page);
        Assert.Contains(page.Entries, entry => entry.Category == "Microsoft.Hosting.Lifetime");
        Assert.Contains(page.Entries, entry => entry.Message == "Application logger is captured");
        Assert.DoesNotContain(page.Entries, entry => entry.Message == "Filtered warning");
        Assert.Contains(page.Entries, entry => entry.Message == "Error above configured minimum");
        Assert.Contains(page.Entries, entry => entry.Message == "Recording recording-01 failed with credentials …");

        logger.LogError("Next log entry");
        var next = await adminBrowser.Client.GetFromJsonAsync<ServerLogPage>($"{LOGS_PATH}?cursor={Uri.EscapeDataString(page.NextCursor)}");
        Assert.Contains(next!.Entries, entry => entry.Message == "Next log entry");
        Assert.All(next.Entries, entry => Assert.True(entry.Id > page.Entries[^1].Id));
        using var invalid = await adminBrowser.Client.GetAsync($"{LOGS_PATH}?cursor=bad");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var duplicate = await adminBrowser.Client.GetAsync($"{LOGS_PATH}?cursor=bad&cursor=bad");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(account => account.Id == admin.Id,
            Builders<UserAccount>.Update.Set(account => account.Role, "User"));
        using var revoked = await adminBrowser.Client.GetAsync(LOGS_PATH);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }
}
