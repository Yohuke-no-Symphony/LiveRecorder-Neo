using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证带宽设置兼容旧配置，并按独立快照持久化。</summary>
public sealed class BandwidthSettingsStoreTests
{
    /// <summary>旧配置缺少带宽设置时默认不限速，并保留下载保证比例的默认值。</summary>
    [Fact]
    public void LegacySettingsDefaultToUnlimitedBandwidth()
    {
        var settings = JsonSerializer.Deserialize<ServerSettings>("""{"quality":400}""", JsonFiles.Options)!;

        Assert.NotNull(settings.Bandwidth);
        Assert.Equal(0, settings.Bandwidth.UserDownloadLimitBytesPerSecond);
        Assert.False(settings.Bandwidth.SmartLimitEnabled);
        Assert.Equal(0, settings.Bandwidth.TotalBytesPerSecond);
        Assert.Equal(10, settings.Bandwidth.DownloadGuaranteedPercent);
    }

    /// <summary>保存后的带宽设置重开仍存在，修改输入或读取快照不会改变已保存值。</summary>
    [Fact]
    public async Task BandwidthSettingsPersistWithoutSharingMutableSnapshots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-bandwidth-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(directory, "Data"), Path.Combine(directory, "records"));
            paths.EnsureDirectories();
            var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            var bootstrap = new BootstrapStore(paths, protector);
            var configFile = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(new { DataDirectory = paths.DataDirectory, RecordsDirectory = paths.RecordsDirectory }));
            var startup = new StartupConfigurationStore(configFile);
            var store = new SettingsStore(startup, bootstrap, new MongoContext(bootstrap), paths);
            var input = new ServerSettings
            {
                Bandwidth = new()
                {
                    UserDownloadLimitBytesPerSecond = 512 * 1024,
                    SmartLimitEnabled = true,
                    TotalBytesPerSecond = 10 * 1024 * 1024,
                    DownloadGuaranteedPercent = 25
                }
            };
            await store.SaveAsync(input);
            input.Bandwidth.UserDownloadLimitBytesPerSecond = 0;
            input.Bandwidth.SmartLimitEnabled = false;
            var snapshot = await store.GetAsync();
            snapshot.Bandwidth.TotalBytesPerSecond = 1;
            snapshot.Bandwidth.DownloadGuaranteedPercent = 99;

            var reloaded = await new SettingsStore(new StartupConfigurationStore(configFile), bootstrap, new MongoContext(bootstrap), paths).GetAsync();

            Assert.Equal(512 * 1024, reloaded.Bandwidth.UserDownloadLimitBytesPerSecond);
            Assert.True(reloaded.Bandwidth.SmartLimitEnabled);
            Assert.Equal(10 * 1024 * 1024, reloaded.Bandwidth.TotalBytesPerSecond);
            Assert.Equal(25, reloaded.Bandwidth.DownloadGuaranteedPercent);
            Assert.Equal(10 * 1024 * 1024, (await store.GetAsync()).Bandwidth.TotalBytesPerSecond);
            Assert.False(File.Exists(Path.Combine(paths.DataDirectory, "settings.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
