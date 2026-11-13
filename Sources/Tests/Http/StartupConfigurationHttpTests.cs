using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Server.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证安装入口配置和数据目录变更在重启前后的实例边界。</summary>
public sealed class StartupConfigurationHttpTests
{
    [MongoHttpFact]
    public async Task MissingStartupConfigurationOpensSetupAndNewInstallationCreatesIt()
    {
        await using var fixture = await HttpServerFixture.CreateWithoutConfigurationAsync();
        Assert.False(File.Exists(fixture.ConfigFile));
        var before = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.False(before!["installed"]!.GetValue<bool>());

        await fixture.InstallAsync();

        var startup = new StartupConfigurationStore(fixture.ConfigFile);
        Assert.True(startup.IsConfigured);
        Assert.Equal(fixture.DataDirectory, startup.ResolvedDataDirectory);
        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        await fixture.RestartAsync();
        var state = await fixture.NewBrowser().Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.True(state!["installed"]!.GetValue<bool>());
    }

    [MongoHttpFact]
    public async Task ExistingDataWithoutStartupConfigRequiresRestoreAndKeepsThePreviousConfiguration()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        settings.Quality = 400;
        await fixture.App.Services.GetRequiredService<ISettingsStore>().SaveAsync(settings);
        await File.WriteAllTextAsync(Path.Combine(fixture.DataDirectory, "settings.json"), JsonSerializer.Serialize(settings, JsonFiles.Options));
        await fixture.RestartWithoutStartupConfigurationAsync();
        var state = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.False(state!["installed"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(fixture.DataDirectory, "bootstrap.json")));

        using var fresh = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest());
        Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
        Assert.False(File.Exists(fixture.ConfigFile));
        using var restore = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest(InstallationMode.Restore));
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        Assert.Equal(400, (await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync()).Quality);
        Assert.True(new StartupConfigurationStore(fixture.ConfigFile).IsConfigured);
        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
    }

    [MongoHttpFact]
    public async Task SavingConfiguredDataDirectoryLeavesTheActiveInstanceUntouchedUntilRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var activeDirectory = fixture.DataDirectory;
        var target = Path.Combine(fixture.DirectoryPath, "restored-data");
        var targetRecords = Path.Combine(fixture.DirectoryPath, "restored-records");
        var activeRecords = fixture.StoragePath;

        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings/paths", new { dataDirectory = target, recordsDirectory = targetRecords });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(activeDirectory, saved!["dataDirectory"]!.GetValue<string>());
        Assert.Equal(target, saved["configuredDataDirectory"]!.GetValue<string>());
        Assert.Equal(activeRecords, saved["recordsDirectory"]!.GetValue<string>());
        Assert.Equal(targetRecords, saved["configuredRecordsDirectory"]!.GetValue<string>());
        Assert.Equal(fixture.ConfigFile, saved["configFile"]!.GetValue<string>());
        Assert.True(saved["restartRequired"]!.GetValue<bool>());
        Assert.Equal(activeDirectory, fixture.App.Services.GetRequiredService<IAppPaths>().DataDirectory);
        Assert.False(Directory.Exists(target));
        var current = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings/paths");
        Assert.Equal(activeDirectory, current!["dataDirectory"]!.GetValue<string>());
        Assert.True(current["restartRequired"]!.GetValue<bool>());

        await fixture.RestartFromCopiedDataAsync(updateConfiguration: false);

        var browser = fixture.NewBrowser();
        await browser.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var restarted = await browser.Client.GetFromJsonAsync<JsonObject>("/api/settings/paths");
        Assert.Equal(target, restarted!["dataDirectory"]!.GetValue<string>());
        Assert.Equal(targetRecords, restarted["recordsDirectory"]!.GetValue<string>());
        Assert.False(restarted["restartRequired"]!.GetValue<bool>());
        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
    }

    [MongoHttpFact]
    public async Task InvalidOrPublicDataDirectoryCannotReplaceTheSavedStartupConfiguration()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var original = await File.ReadAllTextAsync(fixture.ConfigFile);
        var webUi = fixture.App.Services.GetRequiredService<WebUiLocation>();
        foreach (var value in new[] { "", " ", "\0", fixture.ConfigFile, webUi.Path, Path.Combine(webUi.Path, "private-data") })
        {
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings/paths", new { dataDirectory = value, recordsDirectory = fixture.StoragePath });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
            Assert.Equal(fixture.DataDirectory, fixture.App.Services.GetRequiredService<IAppPaths>().DataDirectory);
        }
        foreach (var value in new[] { "", " ", fixture.ConfigFile, webUi.Path, fixture.DataDirectory, Path.Combine(fixture.DataDirectory, "keys") })
        {
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings/paths", new { dataDirectory = fixture.DataDirectory, recordsDirectory = value });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
            Assert.Equal(fixture.StoragePath, fixture.App.Services.GetRequiredService<IAppPaths>().RecordsDirectory);
        }
    }
}
