using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证整个数据目录可迁移，并保持安装配置、密钥和媒体边界。</summary>
public sealed class DataDirectoryHttpTests
{
    [Fact]
    public async Task UninstalledStatusDescribesStartupDirectoriesWithoutConnectingToMongo()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var state = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.False(state!["installed"]!.GetValue<bool>());
        Assert.Equal(fixture.DataDirectory, state["dataDirectory"]!.GetValue<string>());
        Assert.Equal(fixture.StoragePath, state["recordsDirectory"]!.GetValue<string>());
        var paths = fixture.App.Services.GetRequiredService<IAppPaths>();
        Assert.Equal(Path.Combine(fixture.DataDirectory, "keys"), paths.KeysDirectory);
        Assert.Equal(fixture.CachePath, paths.CacheDirectory);
        Assert.Throws<InfrastructureException>(() => MediaPaths.Resolve(paths.RecordsDirectory, "../keys/key.xml"));
    }

    [Fact]
    public async Task CopiedDataPreservesInstallationSettingsSecretsAndRelativeRecordings()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var prepared = fixture.PrepareCommittedStorage();
        var bootstrap = prepared.Bootstrap;
        await bootstrap.SaveAsync(new BootstrapConfiguration
        {
            Installed = true,
            MongoConnectionString = "mongodb://127.0.0.1:1",
            DatabaseName = "directory_migration_test"
        });
        var protector = prepared.SecretProtector;
        var protectedCredential = protector.Protect("migration-test-credential");
        var settings = new ServerSettings { Quality = 400, DefaultLanguage = "ja-JP" };
        settings.Smtp.Password = protector.Protect("migration-smtp-secret");
        await fixture.App.Services.GetRequiredService<ISettingsStore>().SaveAsync(settings);
        var relative = Path.Combine("主播_42", "场次", "录像", "001.flv");
        var originalFile = MediaPaths.Resolve(fixture.StoragePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(originalFile)!);
        await File.WriteAllTextAsync(originalFile, "recording-payload");
        var originalData = fixture.DataDirectory;

        await fixture.RestartFromCopiedDataAsync();

        var browser = fixture.NewBrowser();
        var state = await browser.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.True(state!["installed"]!.GetValue<bool>());
        Assert.False(state["requiresToken"]!.GetValue<bool>());
        Assert.False(state.ContainsKey("dataDirectory"));
        Assert.False(state.ContainsKey("recordsDirectory"));
        Assert.NotEqual(originalData, fixture.DataDirectory);
        var restoredProtector = fixture.App.Services.GetRequiredService<ISecretProtector>();
        Assert.Equal("migration-test-credential", restoredProtector.Unprotect(protectedCredential));
        var restoredSettings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(400, restoredSettings.Quality);
        Assert.Equal("ja-JP", restoredSettings.DefaultLanguage);
        Assert.Equal("migration-smtp-secret", restoredProtector.Unprotect(restoredSettings.Smtp.Password));
        Assert.Equal("recording-payload", await File.ReadAllTextAsync(MediaPaths.Resolve(fixture.StoragePath, relative)));
        Assert.True(File.Exists(originalFile));
        var persisted = await File.ReadAllTextAsync(fixture.ConfigFile);
        Assert.DoesNotContain(originalData, persisted);
        Assert.DoesNotContain("migration-smtp-secret", persisted);
    }

    [Fact]
    public async Task MissingBootstrapKeyCannotReopenInstallation()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var paths = fixture.App.Services.GetRequiredService<IAppPaths>();
        var prepared = fixture.PrepareCommittedStorage();
        var bootstrap = prepared.Bootstrap;
        await bootstrap.SaveAsync(new BootstrapConfiguration { Installed = true });
        var original = await File.ReadAllTextAsync(paths.BootstrapFile);
        var wrongProtector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());

        Assert.Throws<CryptographicException>(() => new BootstrapStore(paths, wrongProtector));

        Assert.Equal(original, await File.ReadAllTextAsync(paths.BootstrapFile));
        Assert.True(new BootstrapStore(paths, prepared.SecretProtector).IsInstalled);
    }
}
