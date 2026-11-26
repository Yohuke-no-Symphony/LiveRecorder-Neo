using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

public sealed class MigrationStartupHttpTests
{
    [Fact]
    public async Task InstalledServerKeepsDiagnosticsWhenItCannotReadTheDataVersion()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var persisted = fixture.PrepareCommittedStorage();
        await persisted.Bootstrap.SaveAsync(new BootstrapConfiguration
        {
            Installed = true,
            DatabaseName = fixture.DatabaseName,
            MongoConnectionString = "mongodb://127.0.0.1:1",
        });
        await fixture.RestartAsync();

        using var health = await fixture.Admin.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var status = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.True(status!["installed"]!.GetValue<bool>());
        var session = await fixture.Admin.RefreshCsrfAsync();
        Assert.Null(session["user"]);
        using var setup = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest());
        Assert.Equal(HttpStatusCode.Conflict, setup.StatusCode);
        using var api = await fixture.Admin.Client.GetAsync("/api/sessions");
        using var hub = await fixture.Admin.SendAsync(HttpMethod.Post, "/hubs/status/negotiate");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, hub.StatusCode);
    }
}
