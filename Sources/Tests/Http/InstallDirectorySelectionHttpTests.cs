using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证安装直接启用所选独立目录，并原子切换密钥、数据库和认证状态。</summary>
public sealed class InstallDirectorySelectionHttpTests
{
    [MongoHttpFact]
    public async Task ExistingSessionCannotExposeBusinessDataWhileARestartedInstanceRequiresRecovery()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var cookies = fixture.Admin.Cookies;
        await fixture.App.Services.GetRequiredService<IBootstrapStore>().SaveAsync(new BootstrapConfiguration
        {
            Installed = false,
            Mode = InstallationMode.Restore,
            MongoConnectionString = fixture.MongoConnectionString,
            DatabaseName = fixture.DatabaseName
        });
        using var stillReady = await fixture.Admin.Client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, stillReady.StatusCode);

        await fixture.RestartAsync();

        var oldSession = fixture.NewBrowser(cookies);
        using var unavailable = await oldSession.Client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var problem = await unavailable.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("SETUP_REQUIRED", problem!["code"]!.GetValue<string>());
        var anonymous = await oldSession.RefreshCsrfAsync();
        Assert.Null(anonymous["user"]);
        Assert.False(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
    }

    [MongoHttpFact]
    public async Task NewIndependentDirectoriesBecomeActiveImmediatelyAndRemainUsableAfterRestart()
    {
        await using var fixture = await HttpServerFixture.CreateWithoutConfigurationAsync();
        var staleBrowser = fixture.NewBrowser();
        await staleBrowser.RefreshCsrfAsync();
        var oldSecret = fixture.App.Services.GetRequiredService<ISecretProtector>().Protect("previous-instance-secret");
        var initialKeys = fixture.App.Services.GetRequiredService<IAppPaths>().KeysDirectory;
        Assert.False(Directory.Exists(initialKeys));
        var data = Path.Combine(fixture.DirectoryPath, "chosen-private-data");
        var records = Path.Combine(fixture.DirectoryPath, "separate-recording-volume", "records");
        Assert.False(Directory.Exists(data));
        Assert.False(Directory.Exists(records));

        await fixture.InstallAsync(fixture.SetupRequest() with { DataDirectory = data, RecordsDirectory = records });

        Assert.Equal(data, fixture.DataDirectory);
        Assert.Equal(records, fixture.StoragePath);
        Assert.True(Directory.Exists(data));
        Assert.True(Directory.Exists(records));
        Assert.True(File.Exists(Path.Combine(data, "bootstrap.json")));
        Assert.False(File.Exists(Path.Combine(data, "settings.json")));
        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        Assert.Equal(fixture.DatabaseName, fixture.Context.Database.DatabaseNamespace.DatabaseName);
        var selectedKeys = Directory.EnumerateFiles(Path.Combine(data, "keys"), "key-*.xml").Select(Path.GetFileName).ToArray();
        Assert.NotEmpty(selectedKeys);
        Assert.False(Directory.Exists(initialKeys));
        Assert.Throws<CryptographicException>(() => fixture.App.Services.GetRequiredService<ISecretProtector>().Unprotect(oldSecret));
        using var staleLogin = await staleBrowser.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = HttpServerFixture.ADMIN_EMAIL, password = HttpServerFixture.ADMIN_PASSWORD });
        Assert.Equal(HttpStatusCode.BadRequest, staleLogin.StatusCode);
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings") ?? throw new InvalidDataException("Missing settings.");
        settings.Quality = 400;
        settings.Smtp.Password = "new-instance-smtp-secret";
        using var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var paths = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings/paths");
        Assert.Equal(data, paths!["dataDirectory"]!.GetValue<string>());
        Assert.Equal(records, paths["recordsDirectory"]!.GetValue<string>());
        Assert.False(paths["restartRequired"]!.GetValue<bool>());
        var main = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigFile))!.AsObject();
        Assert.Equal(data, main["DataDirectory"]!.GetValue<string>());
        Assert.Equal(records, main["RecordsDirectory"]!.GetValue<string>());
        Assert.Equal(400, main["quality"]!.GetValue<int>());
        Assert.NotNull(main["smtp"]);
        Assert.DoesNotContain("new-instance-smtp-secret", main.ToJsonString());

        await fixture.RestartAsync();

        var restarted = fixture.NewBrowser();
        await restarted.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.Equal(data, fixture.DataDirectory);
        Assert.Equal(records, fixture.StoragePath);
        var persisted = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(400, persisted.Quality);
        Assert.Equal("new-instance-smtp-secret", fixture.App.Services.GetRequiredService<ISecretProtector>().Unprotect(persisted.Smtp.Password));
    }

    [Fact]
    public async Task IncorrectInstallationTokenCannotCreateTheRequestedDirectories()
    {
        await using var fixture = await HttpServerFixture.CreateWithoutConfigurationAsync();
        await fixture.Admin.RefreshCsrfAsync();
        var data = Path.Combine(fixture.DirectoryPath, "must-not-create-data");
        var records = Path.Combine(fixture.DirectoryPath, "must-not-create-records");
        var original = fixture.App.Services.GetRequiredService<CurrentInstanceStorage>().Current;
        var request = fixture.SetupRequest() with { Token = "incorrect-installation-token", DataDirectory = data, RecordsDirectory = records };

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(Directory.Exists(data));
        Assert.False(Directory.Exists(records));
        Assert.False(File.Exists(fixture.ConfigFile));
        Assert.Same(original, fixture.App.Services.GetRequiredService<CurrentInstanceStorage>().Current);
    }

    [MongoHttpFact]
    public async Task FailedCandidateValidationPreservesTheActiveInstanceAndMainConfiguration()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.Admin.RefreshCsrfAsync();
        fixture.TrackDatabase();
        var original = fixture.App.Services.GetRequiredService<CurrentInstanceStorage>().Current;
        var main = await File.ReadAllTextAsync(fixture.ConfigFile);
        var secret = fixture.App.Services.GetRequiredService<ISecretProtector>().Protect("still-active");
        var request = fixture.SetupRequest() with
        {
            DataDirectory = Path.Combine(fixture.DirectoryPath, "failed-data"),
            RecordsDirectory = Path.Combine(fixture.DirectoryPath, "failed-records"),
            FfmpegPath = Path.Combine(fixture.DirectoryPath, "missing-ffmpeg-executable")
        };

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Same(original, fixture.App.Services.GetRequiredService<CurrentInstanceStorage>().Current);
        Assert.False(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
        Assert.Equal(main, await File.ReadAllTextAsync(fixture.ConfigFile));
        Assert.Equal("still-active", fixture.App.Services.GetRequiredService<ISecretProtector>().Unprotect(secret));
        Assert.False(Directory.Exists(request.DataDirectory));
        Assert.False(Directory.Exists(request.RecordsDirectory));
    }

    [MongoHttpFact]
    public async Task RestoreUsesOnlyTheSelectedTargetsKeysAndPreservesGlobalSettingsAndAccounts()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        fixture.TrackDatabase();
        var oldSecret = fixture.App.Services.GetRequiredService<ISecretProtector>().Protect("old-default-root");
        var data = Path.Combine(fixture.DirectoryPath, "restored-private-data");
        var records = Path.Combine(fixture.DirectoryPath, "restored-recording-volume");
        var factory = new InstanceStorageFactory();
        var target = factory.CreateForInstallationCommit(factory.CreateCandidate(data, records));
        await target.Bootstrap.SaveAsync(new BootstrapConfiguration { Installed = true, MongoConnectionString = fixture.MongoConnectionString, DatabaseName = fixture.DatabaseName });
        var admin = Administrator(fixture);
        await target.Mongo.Collection<UserAccount>("users").InsertOneAsync(admin);
        var account = new BilibiliAccount { Uid = 42, UserName = "恢复账号", ProtectedCredentials = target.SecretProtector.Protect(JsonSerializer.Serialize(new { Cookie = "SESSDATA=restore-target-session" })) };
        await target.Mongo.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        var settings = new ServerSettings { Quality = 250, DefaultLanguage = "ja-JP" };
        settings.Smtp.Password = target.SecretProtector.Protect("target-smtp-secret");
        await fixture.App.Services.GetRequiredService<ISettingsStore>().SaveAsync(settings);

        await fixture.InstallAsync(fixture.SetupRequest(InstallationMode.Restore) with { DataDirectory = data, RecordsDirectory = records });

        Assert.Equal(data, fixture.DataDirectory);
        Assert.Equal(records, fixture.StoragePath);
        var restored = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(250, restored.Quality);
        Assert.Equal("ja-JP", restored.DefaultLanguage);
        Assert.Equal(settings.Smtp.Password, restored.Smtp.Password);
        Assert.Equal("target-smtp-secret", fixture.App.Services.GetRequiredService<ISecretProtector>().Unprotect(restored.Smtp.Password));
        Assert.Throws<CryptographicException>(() => fixture.App.Services.GetRequiredService<ISecretProtector>().Unprotect(oldSecret));
        Assert.Equal(account.ProtectedCredentials, (await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(value => value.Id == account.Id).SingleAsync()).ProtectedCredentials);
        Assert.Equal(admin.Id, (await fixture.Context.Collection<UserAccount>("users").Find(value => value.Role == "Admin").SingleAsync()).Id);
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.True(fixture.App.Services.GetRequiredService<IBootstrapStore>().IsInstalled);
    }

    [MongoHttpFact]
    public async Task RestoreCannotFallBackToTheWarmActiveKeyRingWhenTheTargetDoesNotHaveItsKeys()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        fixture.TrackDatabase();
        var database = new MongoClient(fixture.MongoConnectionString).GetDatabase(fixture.DatabaseName);
        await database.GetCollection<UserAccount>("users").InsertOneAsync(Administrator(fixture));
        var protector = fixture.App.Services.GetRequiredService<ISecretProtector>();
        var settings = new ServerSettings();
        settings.Smtp.Password = protector.Protect("only-the-old-root-can-decrypt");
        await fixture.App.Services.GetRequiredService<ISettingsStore>().SaveAsync(settings);
        var original = fixture.App.Services.GetRequiredService<CurrentInstanceStorage>().Current;
        var main = await File.ReadAllTextAsync(fixture.ConfigFile);
        await fixture.Admin.RefreshCsrfAsync();
        var request = fixture.SetupRequest(InstallationMode.Restore) with
        {
            DataDirectory = Path.Combine(fixture.DirectoryPath, "target-without-original-keys"),
            RecordsDirectory = Path.Combine(fixture.DirectoryPath, "target-records")
        };

        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/setup", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("SETUP_RESTORE_KEYS_MISSING", problem!["code"]!.GetValue<string>());
        Assert.Same(original, fixture.App.Services.GetRequiredService<CurrentInstanceStorage>().Current);
        Assert.Equal(main, await File.ReadAllTextAsync(fixture.ConfigFile));
        Assert.Equal("only-the-old-root-can-decrypt", protector.Unprotect(settings.Smtp.Password));
        Assert.False(Directory.Exists(request.DataDirectory));
        Assert.False(Directory.Exists(request.RecordsDirectory));
    }

    private static UserAccount Administrator(HttpServerFixture fixture)
    {
        var admin = new UserAccount { Email = HttpServerFixture.ADMIN_EMAIL, NormalizedEmail = HttpServerFixture.ADMIN_EMAIL, DisplayName = "原管理员", Role = "Admin", Status = "Active", EmailVerified = true };
        admin.PasswordHash = fixture.App.Services.GetRequiredService<IPasswordHasher<UserAccount>>().HashPassword(admin, HttpServerFixture.ADMIN_PASSWORD);
        return admin;
    }
}
