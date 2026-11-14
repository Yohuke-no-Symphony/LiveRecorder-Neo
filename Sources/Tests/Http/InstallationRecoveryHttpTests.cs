using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证全新安装和恢复实例互不覆盖，并保留管理员及受保护的原配置。</summary>
public sealed class InstallationRecoveryHttpTests
{
    [Theory]
    [InlineData("email", "AUTH_EMAIL_INVALID")]
    [InlineData("userName", "AUTH_DISPLAY_NAME_LENGTH_INVALID")]
    [InlineData("password", "AUTH_PASSWORD_LENGTH_INVALID")]
    public async Task NewInstallationStillRequiresEveryAdministratorField(string field, string code)
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.Admin.RefreshCsrfAsync();
        var request = JsonSerializer.SerializeToNode(fixture.SetupRequest(), JsonSerializerOptions.Web)!.AsObject();
        request.Remove(field);
        var settings = await File.ReadAllTextAsync(fixture.ConfigFile);

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
        Assert.False(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        Assert.Equal(settings, await File.ReadAllTextAsync(fixture.ConfigFile));
        Assert.False(Directory.Exists(fixture.DataDirectory));
    }

    [MongoHttpFact]
    public async Task InterruptedNewInstallationWithoutAdministratorCanResume()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var bootstrap = fixture.PrepareCommittedStorage().Bootstrap;
        await bootstrap.SaveAsync(new BootstrapConfiguration
        {
            Installed = false,
            Mode = InstallationMode.New,
            MongoConnectionString = fixture.MongoConnectionString,
            DatabaseName = fixture.DatabaseName
        });

        await fixture.RestartAsync();
        await fixture.InstallAsync();

        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        var admin = Assert.Single(await fixture.Context.Collection<UserAccount>("users").Find(FilterDefinition<UserAccount>.Empty).ToListAsync());
        Assert.Equal("Admin", admin.Role);
        Assert.Equal(HttpServerFixture.ADMIN_EMAIL, admin.Email);
        Assert.True(File.Exists(fixture.ConfigFile));
    }

    [MongoHttpFact]
    public async Task InterruptedNewInstallationKeepsItsExistingAdministratorAndSavedSettings()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var original = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Role == "Admin").SingleAsync();
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        settings.Quality = 400;
        settings.Smtp.Password = fixture.App.Services.GetRequiredService<ISecretProtector>().Protect("partial-install-secret");
        await fixture.App.Services.GetRequiredService<ISettingsStore>().SaveAsync(settings);
        await fixture.App.Services.GetRequiredService<IBootstrapStore>().SaveAsync(new BootstrapConfiguration
        {
            Installed = false,
            Mode = InstallationMode.New,
            MongoConnectionString = fixture.MongoConnectionString,
            DatabaseName = fixture.DatabaseName
        });

        await fixture.RestartAsync();
        await fixture.InstallAsync();

        var restored = Assert.Single(await fixture.Context.Collection<UserAccount>("users").Find(FilterDefinition<UserAccount>.Empty).ToListAsync());
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.PasswordHash, restored.PasswordHash);
        var preserved = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(400, preserved.Quality);
        Assert.Equal(settings.Smtp.Password, preserved.Smtp.Password);
    }

    [MongoHttpFact]
    public async Task InstalledLegacyInstanceImportsGlobalSettingsOnceWithoutReopeningSetup()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        settings.Quality = 250;
        var legacy = settings.ToBsonDocument();
        legacy["StoragePath"] = "/legacy-host/recordings";
        await fixture.Context.Database.GetCollection<BsonDocument>("settings").InsertOneAsync(legacy);

        await fixture.RestartAsync(removeLocalSettings: true);

        var browser = fixture.NewBrowser();
        var state = await browser.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.True(state!["installed"]!.GetValue<bool>());
        Assert.Equal(250, (await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync()).Quality);
        var persisted = await File.ReadAllTextAsync(fixture.ConfigFile);
        Assert.DoesNotContain("/legacy-host/recordings", persisted);
        Assert.False(state["requiresToken"]!.GetValue<bool>());
    }

    [MongoHttpFact]
    public async Task InterruptedRestoreCannotBeRetriedAsNewInstallationWithDefaultSettings()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.ReopenSetupAsync(preserveSettings: false);
        var bootstrap = fixture.PrepareCommittedStorage().Bootstrap;
        await bootstrap.SaveAsync(new BootstrapConfiguration
        {
            Installed = false,
            Mode = InstallationMode.Restore,
            MongoConnectionString = fixture.MongoConnectionString,
            DatabaseName = fixture.DatabaseName
        });

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest());

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "SETUP_EXISTING_CONFIGURATION");
        Assert.False(bootstrap.IsInstalled);
        Assert.Equal(InstallationMode.Restore, bootstrap.Current!.Mode);
        Assert.False(new StartupConfigurationStore(fixture.ConfigFile).HasSettings);
    }

    [MongoHttpFact]
    public async Task RestoreWithoutAdministratorFieldsPreservesAccountsConfigurationAndExistingBusinessData()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var admin = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Role == "Admin").SingleAsync();
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        settings.Quality = 400;
        settings.DefaultLanguage = "ja-JP";
        settings.Smtp.Password = fixture.App.Services.GetRequiredService<ISecretProtector>().Protect("restored-smtp-secret");
        await fixture.App.Services.GetRequiredService<ISettingsStore>().SaveAsync(settings);
        var streamer = new Streamer { Name = "保留的主播", Uid = 42, RoomId = 100, Enabled = false };
        await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(streamer);
        await fixture.ReopenSetupAsync();

        using var newInstall = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest());
        Assert.Equal(HttpStatusCode.Conflict, newInstall.StatusCode);
        var request = JsonSerializer.SerializeToNode(fixture.SetupRequest(InstallationMode.Restore), JsonSerializerOptions.Web)!.AsObject();
        request.Remove("userName");
        request.Remove("email");
        request.Remove("password");
        using var restored = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", request);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        var restoredAdmin = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Id == admin.Id).SingleAsync();
        Assert.Equal(admin.DisplayName, restoredAdmin.DisplayName);
        Assert.Equal(admin.PasswordHash, restoredAdmin.PasswordHash);
        Assert.Single(await fixture.Context.Collection<UserAccount>("users").Find(FilterDefinition<UserAccount>.Empty).ToListAsync());
        Assert.Equal(streamer.Id, (await fixture.Context.Collection<Streamer>("streamers").Find(x => x.Id == streamer.Id).SingleAsync()).Id);
        var restoredSettings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(400, restoredSettings.Quality);
        Assert.Equal("ja-JP", restoredSettings.DefaultLanguage);
        Assert.Equal(settings.Smtp.Password, restoredSettings.Smtp.Password);
        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
    }

    [MongoHttpFact]
    public async Task NewInstallationRefusesExistingDatabaseInsteadOfReplacingItsAdministrator()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var original = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Role == "Admin").SingleAsync();
        await fixture.ReopenSetupAsync(preserveSettings: false);

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest());

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "SETUP_DATABASE_NOT_EMPTY");
        var admin = await new MongoClient(fixture.MongoConnectionString).GetDatabase(fixture.DatabaseName).GetCollection<UserAccount>("users").Find(x => x.Id == original.Id).SingleAsync();
        Assert.Equal(original.PasswordHash, admin.PasswordHash);
        Assert.False(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        Assert.False(new StartupConfigurationStore(fixture.ConfigFile).HasSettings);
    }

    [MongoHttpFact]
    public async Task IncorrectRestoreTokenCannotWriteInstallationStateOrConfiguration()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var paths = fixture.App.Services.GetRequiredService<IAppPaths>();
        var settings = await File.ReadAllTextAsync(fixture.ConfigFile);
        await fixture.ReopenSetupAsync();

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest(InstallationMode.Restore) with { Token = "incorrect-installation-token" });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "SETUP_TOKEN_INVALID");
        Assert.False(File.Exists(paths.BootstrapFile));
        Assert.Equal(settings, await File.ReadAllTextAsync(fixture.ConfigFile));
    }

    [MongoHttpFact]
    public async Task RestoreIgnoresSuppliedAdministratorFieldsAndKeepsTheOriginalAccount()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var original = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Role == "Admin").SingleAsync();
        await fixture.ReopenSetupAsync();

        await fixture.InstallAsync(fixture.SetupRequest(InstallationMode.Restore) with { UserName = "不能覆盖原管理员", Email = "not-an-email", Password = "ignored" });

        var admin = Assert.Single(await fixture.Context.Collection<UserAccount>("users").Find(FilterDefinition<UserAccount>.Empty).ToListAsync());
        Assert.Equal(original.Id, admin.Id);
        Assert.Equal(original.Email, admin.Email);
        Assert.Equal(original.DisplayName, admin.DisplayName);
        Assert.Equal(original.PasswordHash, admin.PasswordHash);
    }

    [MongoHttpFact]
    public async Task RestoreWithoutAnActiveAdministratorCannotWriteInstallationStateOrConfiguration()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Context.Collection<UserAccount>("users").UpdateManyAsync(x => x.Role == "Admin", Builders<UserAccount>.Update.Set(x => x.Status, "Disabled"));
        var paths = fixture.App.Services.GetRequiredService<IAppPaths>();
        var settings = await File.ReadAllTextAsync(fixture.ConfigFile);
        await fixture.ReopenSetupAsync();

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest(InstallationMode.Restore));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "SETUP_RESTORE_ADMIN_MISSING");
        Assert.False(File.Exists(paths.BootstrapFile));
        Assert.Equal(settings, await File.ReadAllTextAsync(fixture.ConfigFile));
    }

    [MongoHttpFact]
    public async Task InterruptedNewInstallationStillRequiresTheExistingAdministratorPassword()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var original = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Role == "Admin").SingleAsync();
        var settings = await File.ReadAllTextAsync(fixture.ConfigFile);
        await fixture.App.Services.GetRequiredService<IBootstrapStore>().SaveAsync(new BootstrapConfiguration
        {
            Installed = false,
            Mode = InstallationMode.New,
            MongoConnectionString = fixture.MongoConnectionString,
            DatabaseName = fixture.DatabaseName
        });
        await fixture.RestartAsync();
        await fixture.Admin.RefreshCsrfAsync();

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest(password: "incorrect-administrator-password"));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "SETUP_RESTORE_ADMIN_INVALID");
        Assert.False(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        Assert.Equal(settings, await File.ReadAllTextAsync(fixture.ConfigFile));
        var admin = Assert.Single(await new MongoClient(fixture.MongoConnectionString).GetDatabase(fixture.DatabaseName).GetCollection<UserAccount>("users").Find(FilterDefinition<UserAccount>.Empty).ToListAsync());
        Assert.Equal(original.PasswordHash, admin.PasswordHash);
    }

    [MongoHttpFact]
    public async Task RestoreImportsLegacyMongoSettingsAndDropsTheOldAbsoluteRecordingPath()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        settings.Quality = 250;
        var legacy = settings.ToBsonDocument();
        legacy["StoragePath"] = "/old-host/recordings";
        await fixture.Context.Database.GetCollection<BsonDocument>("settings").InsertOneAsync(legacy);
        await fixture.ReopenSetupAsync(preserveSettings: false);

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest(InstallationMode.Restore));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(250, (await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync()).Quality);
        var persisted = await File.ReadAllTextAsync(fixture.ConfigFile);
        Assert.DoesNotContain("/old-host/recordings", persisted);
        Assert.DoesNotContain("storagePath", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await fixture.Context.Database.GetCollection<BsonDocument>("settings").Find(new BsonDocument("_id", "server")).SingleAsync());
    }

    [MongoHttpFact]
    public async Task RestoreWithoutConfigurationRefusesToReplaceItWithDefaults()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.ReopenSetupAsync(preserveSettings: false);

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest(InstallationMode.Restore));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "SETUP_RESTORE_SETTINGS_MISSING");
        var paths = fixture.App.Services.GetRequiredService<IAppPaths>();
        Assert.False(File.Exists(paths.BootstrapFile));
        Assert.False(new StartupConfigurationStore(fixture.ConfigFile).HasSettings);
    }

    [MongoHttpFact]
    public async Task RestoreWithoutTheOriginalAccountKeyRefusesToComplete()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var protector = fixture.App.Services.GetRequiredService<ISecretProtector>();
        var credentials = new { Cookie = "SESSDATA=restored-session", RefreshToken = "restored-refresh-token" };
        var account = new BilibiliAccount { Uid = 42, UserName = "采集账号", ProtectedCredentials = protector.Protect(JsonSerializer.Serialize(credentials)) };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        var database = fixture.Context.Database;
        await fixture.ReopenSetupAsync(preserveKeys: false);

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", fixture.SetupRequest(InstallationMode.Restore));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "SETUP_RESTORE_KEYS_MISSING");
        Assert.False(File.Exists(fixture.App.Services.GetRequiredService<IAppPaths>().BootstrapFile));
        Assert.Equal(account.ProtectedCredentials, (await database.GetCollection<BilibiliAccount>("bilibili_accounts").Find(x => x.Id == account.Id).SingleAsync()).ProtectedCredentials);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(code, body!["code"]!.GetValue<string>());
    }
}
