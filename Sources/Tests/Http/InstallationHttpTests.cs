using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证安装锁定不会因为数据库故障或重启而失效。</summary>
public sealed class InstallationHttpTests
{
    [MongoHttpFact]
    public async Task CompletedSetupCannotBeOverwritten()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        using var again = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var state = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.True(state!["installed"]!.GetValue<bool>());
        Assert.False(state["requiresToken"]!.GetValue<bool>());
    }

    [MongoHttpFact]
    public async Task MongoOutageAfterRestartDoesNotReopenSetup()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var cookies = fixture.Admin.Cookies;
        var bootstrap = fixture.App.Services.GetRequiredService<IBootstrapStore>();
        await bootstrap.SaveAsync(new BootstrapConfiguration { Installed = true, DatabaseName = fixture.DatabaseName, MongoConnectionString = "mongodb://127.0.0.1:1" });
        await fixture.RestartAsync();
        var anonymous = fixture.NewBrowser();
        var state = await anonymous.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.True(state!["installed"]!.GetValue<bool>());
        Assert.False(state["requiresToken"]!.GetValue<bool>());
        await anonymous.RefreshCsrfAsync();
        using var setup = await anonymous.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest());
        Assert.Equal(HttpStatusCode.Conflict, setup.StatusCode);
        var authenticated = fixture.NewBrowser(cookies);
        using var unavailable = await authenticated.Client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
    }
}
