using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证启动配置的路径基准、额外字段和私有配置边界。</summary>
public sealed class StartupConfigurationStoreTests
{
    [Theory]
    [InlineData("settings.json", "settings.json", "records")]
    [InlineData("settings.json", "settings.json/data", "records")]
    [InlineData("settings.json", "Data", "settings.json/records")]
    [InlineData("settings.json", "Data", "Data/bootstrap.json")]
    [InlineData("settings.json", "Data", "Data/bootstrap.json/records")]
    [InlineData("settings.json", "Data", "Data/installation-token")]
    [InlineData("settings.json", "Data", "Data/installation-token/records")]
    [InlineData("Data/bootstrap.json/startup.json", "..", "../records")]
    [InlineData("Data/installation-token/startup.json", "..", "../records")]
    public async Task FutureConfigurationFilesCannotBeCreatedAsDataOrRecordingDirectories(string configRelative, string data, string records)
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var store = new StartupConfigurationStore(Path.Combine(directory, configRelative));

            var error = await Assert.ThrowsAsync<InfrastructureException>(() => store.SaveAsync(data, records));

            Assert.Equal("STARTUP_CONFIGURATION_PATH_CONFLICT", error.Code);
            Assert.False(store.IsConfigured);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInstallationFinalizationRestoresTheExactPreviousMainFileAndSnapshot(bool hasPreviousFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var configFile = Path.Combine(directory, "settings.json");
            const string ORIGINAL = "{\n  \"DataDirectory\": \"Data\", \"RecordsDirectory\": \"Data/records\", \"quality\": 250, \"CustomFlag\": true\n}";
            if (hasPreviousFile)
            {
                await File.WriteAllTextAsync(configFile, ORIGINAL);
            }
            var store = new StartupConfigurationStore(configFile);
            var finalized = false;

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitInstallationAsync("selected-data", "selected-records", new ServerSettings { Quality = 400 }, _ =>
            {
                finalized = true;
                Assert.True(File.Exists(configFile));
                Assert.Contains("selected-data", File.ReadAllText(configFile));
                throw new InvalidOperationException("Simulated bootstrap finalization failure.");
            }));

            Assert.True(finalized);
            Assert.Equal(hasPreviousFile, File.Exists(configFile));
            if (hasPreviousFile)
            {
                Assert.Equal(ORIGINAL, await File.ReadAllTextAsync(configFile));
                Assert.Equal(250, store.ReadSettings().Quality);
            }
            Assert.Equal("Data", store.DataDirectory);
            Assert.Equal(hasPreviousFile, store.IsConfigured);
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
    public async Task MissingConfigurationUsesDataBesideTheConfigFileUntilExplicitlySaved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configFile = Path.Combine(directory, "configuration", "startup.json");
            var store = new StartupConfigurationStore(configFile);
            Assert.False(store.IsConfigured);
            Assert.Equal("Data", store.DataDirectory);
            Assert.Equal(Path.Combine(directory, "configuration", "Data"), store.ResolvedDataDirectory);
            Assert.False(File.Exists(configFile));

            await store.SaveAsync("../instance", "../recordings");

            Assert.True(store.IsConfigured);
            Assert.Equal(Path.Combine(directory, "instance"), store.ResolvedDataDirectory);
            Assert.Equal(Path.Combine(directory, "recordings"), store.ResolvedRecordsDirectory);
            Assert.Equal("../instance", new StartupConfigurationStore(configFile).DataDirectory);
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
    public async Task RelativeDataUsesTheConfigDirectoryAndSavingPreservesUnknownFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configFile = Path.Combine(directory, "configuration", "startup.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            await File.WriteAllTextAsync(configFile, """{"DataDirectory":"../original","Logging":{"Level":"Debug"},"CustomFlag":42}""");
            var store = new StartupConfigurationStore(configFile);
            Assert.Equal(Path.Combine(directory, "original"), store.ResolvedDataDirectory);
            var externallyUpdated = JsonNode.Parse(await File.ReadAllTextAsync(configFile))!.AsObject();
            externallyUpdated["AddedWhileRunning"] = "preserve-this";
            await File.WriteAllTextAsync(configFile, externallyUpdated.ToJsonString());

            await store.SaveAsync("../migrated", "../recordings");

            var json = JsonNode.Parse(await File.ReadAllTextAsync(configFile))!.AsObject();
            Assert.Equal("../migrated", json["DataDirectory"]!.GetValue<string>());
            Assert.Equal("../recordings", json["RecordsDirectory"]!.GetValue<string>());
            Assert.Equal("Debug", json["Logging"]!["Level"]!.GetValue<string>());
            Assert.Equal(42, json["CustomFlag"]!.GetValue<int>());
            Assert.Equal("preserve-this", json["AddedWhileRunning"]!.GetValue<string>());
            Assert.Equal(Path.Combine(directory, "migrated"), new StartupConfigurationStore(configFile).ResolvedDataDirectory);
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
    public async Task SavingCannotOverwriteStartupJsonThatBecameCorruptedWhileRunning()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var configFile = Path.Combine(directory, "startup.json");
            await File.WriteAllTextAsync(configFile, """{"DataDirectory":"Data"}""");
            var store = new StartupConfigurationStore(configFile);
            const string CORRUPTED = "{ corrupted startup configuration";
            await File.WriteAllTextAsync(configFile, CORRUPTED);

            await Assert.ThrowsAsync<JsonException>(() => store.SaveAsync("other-data", "other-records"));

            Assert.Equal(CORRUPTED, await File.ReadAllTextAsync(configFile));
            Assert.Equal("Data", store.DataDirectory);
            Assert.True(store.IsConfigured);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Quality\":400}")]
    public async Task ExistingJsonWithoutAnExplicitDataDirectoryCannotBeTreatedAsAStartupConfig(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var configFile = Path.Combine(directory, "startup.json");
            await File.WriteAllTextAsync(configFile, json);

            Assert.Throws<JsonException>(() => new StartupConfigurationStore(configFile));

            Assert.Equal(json, await File.ReadAllTextAsync(configFile));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("bootstrap.json")]
    [InlineData("installation-token")]
    [InlineData("keys/startup.json")]
    [InlineData("cache/startup.json")]
    [InlineData("records/startup.json")]
    public async Task DataCannotOverlapTheStartupFileWithReservedInstanceFilesOrDirectories(string relativeConfig)
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(directory, "Data");
            var configFile = Path.Combine(data, relativeConfig);
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            var original = JsonSerializer.Serialize(new { DataDirectory = Path.Combine(directory, "other-data"), CustomFlag = true });
            await File.WriteAllTextAsync(configFile, original);
            var store = new StartupConfigurationStore(configFile);

            var error = await Assert.ThrowsAsync<InfrastructureException>(() => store.SaveAsync(data, Path.Combine(data, "records")));

            Assert.Equal("STARTUP_CONFIGURATION_PATH_CONFLICT", error.Code);
            Assert.Equal(original, await File.ReadAllTextAsync(configFile));
            Assert.Equal(Path.Combine(directory, "other-data"), store.ResolvedDataDirectory);
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
    public async Task TheSingleMainSettingsFileCanLiveAtTheDataRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configFile = Path.Combine(directory, "settings.json");
            var store = new StartupConfigurationStore(configFile);

            await store.SaveAsync(".", "records");

            Assert.Equal(directory, store.ResolvedDataDirectory);
            Assert.Equal(Path.Combine(directory, "records"), store.ResolvedRecordsDirectory);
            Assert.Equal(".", new StartupConfigurationStore(configFile).DataDirectory);
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
    public async Task DataCannotBeExposedInsideTheRecordingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-startup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StartupConfigurationStore(Path.Combine(directory, "settings.json"));
            var records = Path.Combine(directory, "records");

            var error = await Assert.ThrowsAsync<InfrastructureException>(() => store.SaveAsync(Path.Combine(records, "private-data"), records));

            Assert.Equal("STARTUP_CONFIGURATION_PATH_CONFLICT", error.Code);
            Assert.False(File.Exists(store.ConfigFile));
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
