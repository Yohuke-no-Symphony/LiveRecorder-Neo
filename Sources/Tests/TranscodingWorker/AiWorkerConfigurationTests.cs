using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证 AI 节点使用独立配置、保留通用租约协议且不依赖媒体工具。</summary>
public sealed class AiWorkerConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ai-worker-configuration-tests", Guid.NewGuid().ToString("N"));

    /// <summary>API 中只暴露 AI 节点使用的资源字段。</summary>
    [Fact]
    public void AiConfigurationExposesOnlyItsOwnFields()
    {
        var configuration = WorkerConfigurationProjection.ToLegacy(new AiWorkerConfiguration { MaxConcurrentJobs = 3 });
        var projected = WorkerConfigurationProjection.Project(configuration, "AI");

        Assert.Equal(new[] { "downloadBytesPerSecond", "maxCacheBytes", "maxConcurrentJobs", "minimumFreeBytes", "tempDirectory", "uploadBytesPerSecond" },
            projected.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(3, projected.GetProperty("maxConcurrentJobs").GetInt32());
        Assert.False(projected.TryGetProperty("ffmpegPath", out _));
        Assert.False(projected.TryGetProperty("maxConcurrentEncodes", out _));
    }

    /// <summary>新节点和缺少本地设置的 AI 节点都默认只运行一个任务。</summary>
    [Fact]
    public async Task NewAiConfigurationsDefaultToOneJob()
    {
        Assert.Equal(1, WorkerConfigurationProjection.Default("AI").MaxConcurrentEncodes);
        using var store = new WorkerSettingsStore(Path.Combine(directory, "settings.json"), "AI");

        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, store.Current.Configuration.MaxConcurrentEncodes);
    }

    /// <summary>独立 maxConcurrentJobs 名称可保存并重新读取，且不向本地配置混入媒体设置。</summary>
    [Fact]
    public async Task AiSettingsRoundTripPreservesJobConcurrency()
    {
        var path = Path.Combine(directory, "settings.json");
        using (var store = new WorkerSettingsStore(path, "AI"))
        {
            await store.SaveAsync(new()
            {
                Configuration = WorkerConfigurationProjection.ToLegacy(new AiWorkerConfiguration
                {
                    TempDirectory = directory, MaxConcurrentJobs = 4, DownloadBytesPerSecond = 12345
                })
            }, CancellationToken.None);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var configuration = json.RootElement.GetProperty("Configuration");
            Assert.Equal(4, configuration.GetProperty("MaxConcurrentJobs").GetInt32());
            Assert.False(configuration.TryGetProperty("FfmpegPath", out _));
        }
        using var reloaded = new WorkerSettingsStore(path, "AI");

        await reloaded.LoadAsync(CancellationToken.None);

        Assert.Equal(4, reloaded.Current.Configuration.MaxConcurrentEncodes);
        Assert.Equal(12345, reloaded.Current.Configuration.DownloadBytesPerSecond);
    }

    /// <summary>公共线协议仍接受原并发字段，独立 API 字段具有明确映射。</summary>
    [Theory]
    [InlineData("{\"maxConcurrentJobs\":5}", 5)]
    [InlineData("{\"MaxConcurrentJobs\":6}", 6)]
    [InlineData("{\"maxConcurrentEncodes\":7}", 7)]
    public void AiConfigurationReadsPublicAndLegacyConcurrencyNames(string content, int expected)
    {
        using var json = JsonDocument.Parse(content);

        var configuration = WorkerConfigurationProjection.Read(json.RootElement, "AI");

        Assert.Equal(expected, configuration.MaxConcurrentEncodes);
    }

    /// <summary>AI 配置不验证 FFmpeg、编码线程和 GPU 字段。</summary>
    [Fact]
    public void MediaSettingsDoNotAffectAiValidationOrConfigurationIdentity()
    {
        var configuration = WorkerConfigurationProjection.ToLegacy(new AiWorkerConfiguration { TempDirectory = directory });
        var unusedMedia = configuration with
        {
            FfmpegPath = "", FfprobePath = "", PreferredEncoder = "unused", GpuConcurrency = -10,
            CpuConcurrency = -10, ClipEncodingThreads = -10
        };

        WorkerConfigurationValidation.Validate(unusedMedia, "AI");

        Assert.Equal(WorkerConfigurationProjection.ComputeHash(configuration, "AI"),
            WorkerConfigurationProjection.ComputeHash(unusedMedia, "AI"));
        Assert.Throws<ArgumentException>(() => WorkerConfigurationPolicy.Validate(configuration with { MaxConcurrentEncodes = 0 }, "AI"));
    }

    /// <summary>新绑定不会把默认本地并发误认为用户覆盖并替换后台新配置。</summary>
    [Fact]
    public void FreshLocalDefaultsDoNotOverrideServerAiConfiguration()
    {
        var settings = new WorkerSettings { Configuration = WorkerConfigurationProjection.Default("AI") };
        var server = new WorkerWelcome
        {
            ConfigurationRevision = 4,
            Configuration = WorkerConfigurationProjection.Default("AI") with { MaxConcurrentEncodes = 3 }
        };

        Assert.Null(WorkerSettingsSynchronization.CreateUpdate(settings, server, "AI"));
    }

    /// <summary>连接配置存在但资源配置缺省时同样采用 AI 自身默认值。</summary>
    [Fact]
    public async Task ConnectionOnlySettingsUseAiDefaults()
    {
        using var store = new WorkerSettingsStore(Path.Combine(directory, "settings.json"), "AI");
        await File.WriteAllTextAsync(store.Path, "{\"Worker\":{\"WorkerId\":\"ai-worker\",\"Secret\":\"secret\"}}");

        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, store.Current.Configuration.MaxConcurrentEncodes);
    }

    /// <summary>共享 AI 数据类型来自无 Domain 引用的纯契约程序集。</summary>
    [Fact]
    public void AiContractsHaveNoDomainOrMediaAssemblyDependency()
    {
        var assembly = typeof(AiAssignment).Assembly;

        Assert.Same(assembly, typeof(AiEvidence).Assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Yohuke.LiveRecorder.Domain" or "Yohuke.LiveRecorder.Media");
        Assert.Null(typeof(AiAssignment).GetProperty("ApiKey"));
        Assert.Null(typeof(AiWorkerConfiguration).GetProperty("Provider"));
        Assert.Null(typeof(AiExecutionSettings).GetProperty("ApiKey"));
    }

    /// <summary>缺少恢复增量字段时保守认定请求已发出，恢复协议保留通用租约证明。</summary>
    [Fact]
    public void MissingRecoveryDispatchProofRemainsConservative()
    {
        var heartbeat = JsonSerializer.Deserialize<WorkerHeartbeat>("{\"aiRecoveries\":[{\"jobId\":\"ai-job\",\"attempt\":1}]}", WorkerProtocol.JsonOptions)!;

        Assert.True(Assert.Single(heartbeat.AiRecoveries).HasDispatchedRequest);
        Assert.Empty(heartbeat.RecoveryRequests);
        Assert.Empty(heartbeat.Jobs);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
