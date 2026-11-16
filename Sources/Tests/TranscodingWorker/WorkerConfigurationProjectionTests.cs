using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>Verifies independent user configuration shapes and compatibility with previously saved complete protocol models.</summary>
public sealed class WorkerConfigurationProjectionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "worker-projection-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Each kind exposes only settings implemented by its executable.</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    [InlineData("Recording")]
    public void ApiConfigurationContainsOnlyTheIndependentKindModel(string kind)
    {
        var configuration = Configuration();
        var projected = WorkerConfigurationProjection.Project(configuration, kind);

        Assert.Equal(ExpectedFields(kind).Order(), projected.EnumerateObject().Select(property => property.Name).Order());
        var legacy = JsonSerializer.SerializeToElement(configuration, WorkerProtocol.JsonOptions);
        foreach (var property in projected.EnumerateObject())
        {
            Assert.Equal(legacy.GetProperty(property.Name).GetRawText(), property.Value.GetRawText());
        }
        Assert.Equal(configuration.MaxConcurrentEncodes, projected.GetProperty("maxConcurrentEncodes").GetInt32());
        if (kind == "Clipping")
        {
            Assert.Equal(7, projected.GetProperty("clipEncodingThreads").GetInt32());
        }
    }

    /// <summary>Generated local JSON contains the same task-specific fields and can be reloaded without restoring unused user settings.</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    [InlineData("Recording")]
    public async Task LocalSettingsContainOnlyTheSelectedKindConfiguration(string kind)
    {
        var path = Path.Combine(directory, kind + ".json");
        using (var store = new WorkerSettingsStore(path, kind))
        {
            await store.SaveAsync(Settings(), CancellationToken.None);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var actual = json.RootElement.GetProperty("Configuration").EnumerateObject()
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            Assert.Equal(ExpectedFields(kind).Order(), actual.Order());
        }
        using var reloaded = new WorkerSettingsStore(path, kind);
        await reloaded.LoadAsync(CancellationToken.None);

        Assert.Equal(WorkerConfigurationProjection.Normalize(Configuration(), kind), reloaded.Current.Configuration);
    }

    /// <summary>Legacy full JSON and its old fingerprint migrate without resubmitting a local override after a remote update.</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    [InlineData("Recording")]
    public async Task LegacyConfigurationHashDoesNotCreateAFalseLocalOverride(string kind)
    {
        var path = Path.Combine(directory, kind + "-legacy.json");
        var original = Settings();
        var accepted = WorkerSettingsSynchronization.Accept(original, original.Configuration, 3, kind);
        var oldConfiguration = JsonSerializer.SerializeToNode(original.Configuration)!.AsObject();
        oldConfiguration.Remove(nameof(WorkerConfiguration.ClipEncodingThreads));
        var legacy = JsonSerializer.SerializeToNode(accepted)!.AsObject();
        legacy[nameof(WorkerSettings.Configuration)] = oldConfiguration.DeepClone();
        legacy[nameof(WorkerSettings.ConfigurationHash)] = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldConfiguration)));
        var remote = new WorkerWelcome { ConfigurationRevision = 4, Configuration = original.Configuration with { MaxConcurrentEncodes = 5 } };
        using (var store = new WorkerSettingsStore(path, kind))
        {
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            await store.LoadAsync(CancellationToken.None);

            Assert.Null(WorkerSettingsSynchronization.CreateUpdate(store.Current, remote, kind));
            await store.SaveRemoteConfigurationAsync(remote.Configuration, 4, CancellationToken.None);
            Assert.Equal(5, store.Current.Configuration.MaxConcurrentEncodes);
        }
        using var restarted = new WorkerSettingsStore(path, kind);
        await restarted.LoadAsync(CancellationToken.None);

        Assert.Null(WorkerSettingsSynchronization.CreateUpdate(restarted.Current, remote, kind));
        Assert.Null(WorkerSettingsSynchronization.CreateUpdate(restarted.Current, remote with
        {
            ConfigurationRevision = 5, Configuration = remote.Configuration with { MaxConcurrentEncodes = 6 }
        }, kind));
        var before = restarted.Current;
        await restarted.SaveRemoteConfigurationAsync(remote.Configuration, 4, CancellationToken.None);
        Assert.Same(before, restarted.Current);
    }

    /// <summary>Unused transport defaults and edits never affect the per-kind revision or equality decision.</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    [InlineData("Recording")]
    public void IrrelevantFieldsCannotCreateAConfigurationConflict(string kind)
    {
        var source = Configuration();
        var settings = WorkerSettingsSynchronization.Accept(Settings(), source, 3, kind);
        settings = settings with
        {
            Configuration = kind == "Transcoding"
                ? settings.Configuration with { ClipEncodingThreads = 53 }
                : settings.Configuration with { GpuConcurrency = 63, CpuThreadFraction = 0.95, DeviceId = "unused-device" }
        };

        var update = WorkerSettingsSynchronization.CreateUpdate(settings, new()
        {
            ConfigurationRevision = 4, Configuration = source with { MaxConcurrentEncodes = 5 }
        }, kind);

        Assert.Null(update);
    }

    /// <summary>A local edit to a real kind-specific field still carries its original revision and rejects concurrent remote changes.</summary>
    [Theory]
    [InlineData("Transcoding")]
    [InlineData("Clipping")]
    [InlineData("Recording")]
    public void RelevantLocalEditsKeepTheirOptimisticConcurrencyBoundary(string kind)
    {
        var configuration = Configuration();
        var settings = WorkerSettingsSynchronization.Accept(Settings(), configuration, 3, kind);
        settings = settings with { Configuration = settings.Configuration with { MaxConcurrentEncodes = 4 } };

        var update = WorkerSettingsSynchronization.CreateUpdate(settings, new()
        {
            Configuration = configuration, ConfigurationRevision = 3
        }, kind);

        Assert.NotNull(update);
        Assert.Equal(4, update.InitialConfiguration!.MaxConcurrentEncodes);
        Assert.Equal(3, update.ExpectedConfigurationRevision);
        Assert.Throws<InvalidOperationException>(() => WorkerSettingsSynchronization.CreateUpdate(settings,
            new() { Configuration = configuration, ConfigurationRevision = 4 }, kind));
    }

    /// <summary>Only clipping validates and stores its Exact encoding thread setting.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void ExactThreadLimitsApplyOnlyToClipping(int threads)
    {
        var configuration = Configuration() with { ClipEncodingThreads = threads };

        Assert.Throws<ArgumentException>(() => WorkerConfigurationPolicy.Validate(configuration, "Clipping"));
        WorkerConfigurationPolicy.Validate(configuration, "Transcoding");
        WorkerConfigurationPolicy.Validate(configuration, "Recording");
    }

    private static WorkerConfiguration Configuration() => new()
    {
        TempDirectory = Path.GetTempPath(), MaxConcurrentEncodes = 3, GpuConcurrency = 3, CpuConcurrency = 2,
        CpuFallbackMode = "Disabled", GpuWaitSeconds = 73, CpuLoadThreshold = 50, CpuSampleWindowSeconds = 60,
        CpuThreadFraction = 0.75, PreferredEncoder = "h264_nvenc", DeviceId = "gpu-device", ClipEncodingThreads = 7,
        MaxCacheBytes = 80L * 1024 * 1024, MinimumFreeBytes = 0, DownloadBytesPerSecond = 1200, UploadBytesPerSecond = 2400
    };

    private static WorkerSettings Settings() => new()
    {
        Worker = new() { WorkerId = "worker", Secret = "secret", ControlHost = "recorder.example.test" },
        Configuration = Configuration()
    };

    private static IEnumerable<string> ExpectedFields(string kind) => kind switch
    {
        "Transcoding" => ["ffmpegPath", "ffprobePath", "tempDirectory", "maxConcurrentEncodes", "gpuConcurrency", "cpuConcurrency",
            "cpuFallbackMode", "gpuWaitSeconds", "cpuLoadThreshold", "cpuSampleWindowSeconds", "cpuThreadFraction", "preferredEncoder",
            "deviceId", "maxCacheBytes", "minimumFreeBytes", "downloadBytesPerSecond", "uploadBytesPerSecond"],
        "Clipping" => ["ffmpegPath", "ffprobePath", "tempDirectory", "maxConcurrentEncodes", "clipEncodingThreads", "maxCacheBytes",
            "minimumFreeBytes", "downloadBytesPerSecond", "uploadBytesPerSecond"],
        "Recording" => ["tempDirectory", "maxConcurrentEncodes", "maxCacheBytes", "minimumFreeBytes", "uploadBytesPerSecond"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
