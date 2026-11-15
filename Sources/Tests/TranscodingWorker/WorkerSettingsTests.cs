using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证唯一设置文件的持久化、绑定身份和主站版本同步。</summary>
public sealed class WorkerSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "worker-settings-tests", Guid.NewGuid().ToString("N"));

    /// <summary>首次运行不依赖模板，完整设置可以重新加载且只有所有者能读取本地凭据。</summary>
    [Fact]
    public async Task MissingSettingsCanBeCreatedAndReloadedWithAllParameters()
    {
        var path = Path.Combine(directory, "settings.json");
        using (var store = new WorkerSettingsStore(path))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Empty(store.Current.Worker.WorkerId);
            Assert.False(File.Exists(path));
            await store.SaveAsync(Settings(), CancellationToken.None);
            Assert.Equal("worker", store.Current.Worker.WorkerId);
        }
        using var reloaded = new WorkerSettingsStore(path);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Equal(Settings(), reloaded.Current);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    /// <summary>旧连接配置改名为 settings 后仍可读取，运行参数从主站补齐。</summary>
    [Fact]
    public async Task ConnectionOnlySettingsAcceptExistingServerConfiguration()
    {
        using var store = new WorkerSettingsStore(Path.Combine(directory, "settings.json"));
        await File.WriteAllTextAsync(store.Path, "{\"worker\":{\"WorkerId\":\"worker\",\"Secret\":\"secret\"}}");
        await store.LoadAsync(CancellationToken.None);
        Assert.Null(WorkerSettingsSynchronization.CreateUpdate(store.Current, Welcome()));
        await store.SaveRemoteConfigurationAsync(Welcome().Configuration, 3, CancellationToken.None);
        Assert.Equal("secret", store.Current.Worker.Secret);
        Assert.Equal(3, store.Current.ConfigurationRevision);
        Assert.Equal(Welcome().Configuration, store.Current.Configuration);
    }

    /// <summary>取消写入不破坏旧配置，也不会留下含密钥的临时文件。</summary>
    [Fact]
    public async Task CancelledSavePreservesExistingSettings()
    {
        using var store = new WorkerSettingsStore(Path.Combine(directory, "settings.json"));
        await store.SaveAsync(Settings(), CancellationToken.None);
        var before = await File.ReadAllTextAsync(store.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Settings() with
        {
            Worker = Settings().Worker with { Secret = "replacement" }
        }, cancellation.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(store.Path));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        Assert.Equal("secret", store.Current.Worker.Secret);
    }

    /// <summary>损坏文件和空配置节不能被当成首次安装而覆盖。</summary>
    [Theory]
    [InlineData("{broken", typeof(JsonException))]
    [InlineData("null", typeof(InvalidDataException))]
    [InlineData("{\"Worker\":null}", typeof(InvalidDataException))]
    [InlineData("{\"Configuration\":null}", typeof(InvalidDataException))]
    [InlineData("{\"ConfigurationRevision\":-1}", typeof(InvalidDataException))]
    public async Task InvalidSettingsRemainUntouched(string content, Type errorType)
    {
        using var store = new WorkerSettingsStore(Path.Combine(directory, "settings.json"));
        await File.WriteAllTextAsync(store.Path, content);
        var error = await Record.ExceptionAsync(() => store.LoadAsync(CancellationToken.None));
        Assert.IsType(errorType, error);
        Assert.Equal(content, await File.ReadAllTextAsync(store.Path));
    }

    /// <summary>运行期间另一进程不能同时修改同一份设置文件。</summary>
    [Fact]
    public void SettingsFileHasExclusiveOwnership()
    {
        using var store = new WorkerSettingsStore(Path.Combine(directory, "settings.json"));
        Assert.Throws<IOException>(() => new WorkerSettingsStore(store.Path));
    }

    /// <summary>没有本地修改时，后台更新会落盘而不会被旧副本覆盖。</summary>
    [Fact]
    public async Task RemoteUpdatesPreserveBindingAndDoNotRewriteIdenticalSettings()
    {
        using var store = new WorkerSettingsStore(Path.Combine(directory, "settings.json"));
        await store.SaveAsync(Synchronized(), CancellationToken.None);
        var newer = Welcome() with { ConfigurationRevision = 4, Configuration = Welcome().Configuration with { MaxConcurrentEncodes = 4 } };
        Assert.Null(WorkerSettingsSynchronization.CreateUpdate(store.Current, newer));
        await store.SaveRemoteConfigurationAsync(newer.Configuration, newer.ConfigurationRevision, CancellationToken.None);
        var current = store.Current;
        await store.SaveRemoteConfigurationAsync(newer.Configuration, newer.ConfigurationRevision, CancellationToken.None);
        Assert.Same(current, store.Current);
        Assert.Equal("secret", store.Current.Worker.Secret);
        Assert.Equal(4, store.Current.Configuration.MaxConcurrentEncodes);
    }

    /// <summary>本地参数修改携带其读取版本上传，双方同时修改时拒绝覆盖。</summary>
    [Fact]
    public void LocalEditsRequireTheOriginalServerRevision()
    {
        var settings = Synchronized() with { Configuration = Welcome().Configuration with { GpuWaitSeconds = 15 } };
        var update = WorkerSettingsSynchronization.CreateUpdate(settings, Welcome());
        Assert.NotNull(update);
        Assert.Equal(3, update.ExpectedConfigurationRevision);
        Assert.Equal(15, update.InitialConfiguration!.GpuWaitSeconds);
        Assert.Throws<InvalidOperationException>(() => WorkerSettingsSynchronization.CreateUpdate(settings,
            Welcome() with { ConfigurationRevision = 4 }));
    }

    /// <summary>主站已接收而响应丢失时，不重复提交相同配置。</summary>
    [Fact]
    public void LostAcknowledgementDoesNotCauseAnotherConfigurationWrite()
    {
        var settings = Synchronized() with { Configuration = Welcome().Configuration with { GpuWaitSeconds = 15 } };
        Assert.Null(WorkerSettingsSynchronization.CreateUpdate(settings,
            new() { Configuration = settings.Configuration, ConfigurationRevision = 4 }));
    }

    /// <summary>密钥轮换保留版本约束，改绑节点时不沿用旧节点版本。</summary>
    [Fact]
    public void CredentialRotationAndRebindingHaveDifferentVersionBoundaries()
    {
        var settings = Synchronized() with { Configuration = Welcome().Configuration with { GpuWaitSeconds = 15 } };
        var rotated = settings with { Worker = settings.Worker with { Secret = "rotated" } };
        Assert.Throws<InvalidOperationException>(() => WorkerSettingsSynchronization.CreateUpdate(rotated,
            Welcome() with { ConfigurationRevision = 1 }));
        var rebound = rotated with { Worker = rotated.Worker with { WorkerId = "other-worker" } };
        var update = WorkerSettingsSynchronization.CreateUpdate(rebound, Welcome() with { ConfigurationRevision = 1 });
        Assert.Equal("other-worker", update!.WorkerId);
        Assert.Equal("rotated", update.Secret);
        Assert.Equal(1, update.ExpectedConfigurationRevision);
    }

    /// <summary>本地手工写入的无效参数不能绕过运行配置校验。</summary>
    [Fact]
    public void InvalidLocalConfigurationIsRejectedBeforeUpload()
    {
        var settings = Synchronized() with { Configuration = Welcome().Configuration with { TempDirectory = "relative" } };
        Assert.Throws<InvalidDataException>(() => WorkerSettingsSynchronization.CreateUpdate(settings, Welcome()));
    }

    private static WorkerWelcome Welcome() => new()
    {
        ConfigurationRevision = 3,
        Configuration = new() { TempDirectory = Path.GetTempPath(), MaxConcurrentEncodes = 3, GpuWaitSeconds = 45 }
    };

    private static WorkerSettings Settings() => new()
    {
        Worker = new() { WorkerId = "worker", Secret = "secret", ControlHost = "recorder.example.test" },
        Configuration = Welcome().Configuration
    };

    private static WorkerSettings Synchronized() => WorkerSettingsSynchronization.Accept(Settings(), Welcome().Configuration, 3);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
