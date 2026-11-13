using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证本地设置在显式保存后才生效，且损坏文件不会被默认配置覆盖。</summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public async Task CancelledPrivateFileReplacementPreservesTheOriginalAndRemovesItsTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var configFile = Path.Combine(directory, "settings.json");
            await PrivateFiles.WriteAllTextAsync(configFile, "original-configuration");
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrivateFiles.WriteAllTextAsync(configFile, "replacement", cancellation.Token));

            Assert.Equal("original-configuration", await File.ReadAllTextAsync(configFile));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task MutatingAnInstallationWriteDoesNotPublishAnUnsavedInstalledState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(directory, "Data"), Path.Combine(directory, "records"));
            paths.EnsureDirectories();
            var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            var bootstrap = new BootstrapStore(paths, protector);
            var configuration = new BootstrapConfiguration { Installed = false };
            await bootstrap.SaveAsync(configuration);

            configuration.Installed = true;

            Assert.False(bootstrap.IsInstalled);
            Assert.False(new BootstrapStore(paths, protector).IsInstalled);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task PersistedSettingsReloadWithoutMongoAndDoNotShareMutableSnapshots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(directory, "Data"), Path.Combine(directory, "records"));
            paths.EnsureDirectories();
            var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            var bootstrap = new BootstrapStore(paths, protector);
            var startup = CreateStartupConfiguration(paths);
            var context = new MongoContext(bootstrap);
            var store = new SettingsStore(startup, bootstrap, context, paths);
            var input = new ServerSettings { Quality = 400 };
            input.Smtp.Password = protector.Protect("local-settings-secret");
            await store.SaveAsync(input);
            input.Quality = 250;
            var snapshot = await store.GetAsync();
            Assert.Equal(400, snapshot.Quality);
            snapshot.Quality = 80;
            snapshot.Smtp.Password = "unsaved";

            var reloaded = await new SettingsStore(new StartupConfigurationStore(startup.ConfigFile), bootstrap, context, paths).GetAsync();

            Assert.Equal(400, (await store.GetAsync()).Quality);
            Assert.Equal(400, reloaded.Quality);
            Assert.Equal("local-settings-secret", protector.Unprotect(reloaded.Smtp.Password));
            var persisted = await File.ReadAllTextAsync(startup.ConfigFile);
            Assert.DoesNotContain("local-settings-secret", persisted);
            Assert.False(File.Exists(Path.Combine(paths.DataDirectory, "settings.json")));
            Assert.Equal(paths.DataDirectory, new StartupConfigurationStore(startup.ConfigFile).ResolvedDataDirectory);
            Assert.Equal(paths.RecordsDirectory, new StartupConfigurationStore(startup.ConfigFile).ResolvedRecordsDirectory);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(startup.ConfigFile));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task CorruptedLocalSettingsDoNotFallBackToDefaultsOrReopenInstallation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(directory, "Data"), Path.Combine(directory, "records"));
            paths.EnsureDirectories();
            var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            var bootstrap = new BootstrapStore(paths, protector);
            var startup = CreateStartupConfiguration(paths);
            await bootstrap.SaveAsync(new BootstrapConfiguration { Installed = true });
            const string CORRUPTED = "{ invalid configuration";
            await File.WriteAllTextAsync(startup.ConfigFile, CORRUPTED);

            Assert.Throws<JsonException>(() => new StartupConfigurationStore(startup.ConfigFile));

            Assert.Equal(CORRUPTED, await File.ReadAllTextAsync(startup.ConfigFile));
            Assert.True(new BootstrapStore(paths, protector).IsInstalled);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
    [Fact]
    public async Task LegacyDataSettingsAreImportedIntoTheSingleMainConfigurationWithoutLosingSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(directory, "Data"), Path.Combine(directory, "Data", "records"));
            paths.EnsureDirectories();
            var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            var bootstrap = new BootstrapStore(paths, protector);
            await bootstrap.SaveAsync(new BootstrapConfiguration { Installed = true, MongoConnectionString = "mongodb://127.0.0.1:1" });
            var configFile = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(configFile, """{"DataDirectory":"Data","CustomFlag":42}""");
            var legacy = new ServerSettings { Quality = 250, DefaultLanguage = "ja-JP" };
            legacy.Smtp.Password = protector.Protect("legacy-smtp-secret");
            var legacyFile = Path.Combine(paths.DataDirectory, "settings.json");
            var original = JsonSerializer.Serialize(legacy, JsonFiles.Options);
            await File.WriteAllTextAsync(legacyFile, original);
            var startup = new StartupConfigurationStore(configFile);
            Assert.False(startup.HasSettings);
            var store = new SettingsStore(startup, bootstrap, new MongoContext(bootstrap), paths);

            var imported = await store.GetAsync();

            Assert.Equal(250, imported.Quality);
            Assert.Equal("ja-JP", imported.DefaultLanguage);
            Assert.Equal("legacy-smtp-secret", protector.Unprotect(imported.Smtp.Password));
            var persisted = new StartupConfigurationStore(configFile);
            Assert.True(persisted.HasSettings);
            Assert.Equal(250, persisted.ReadSettings().Quality);
            Assert.Equal(paths.DataDirectory, persisted.ResolvedDataDirectory);
            Assert.Equal(paths.RecordsDirectory, persisted.ResolvedRecordsDirectory);
            Assert.Equal(original, await File.ReadAllTextAsync(legacyFile));
            Assert.DoesNotContain("legacy-smtp-secret", await File.ReadAllTextAsync(configFile));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static StartupConfigurationStore CreateStartupConfiguration(AppPaths paths)
    {
        var configFile = Path.Combine(Path.GetDirectoryName(paths.DataDirectory)!, "settings.json");
        File.WriteAllText(configFile, JsonSerializer.Serialize(new { DataDirectory = paths.DataDirectory, RecordsDirectory = paths.RecordsDirectory }));
        return new(configFile);
    }
}
