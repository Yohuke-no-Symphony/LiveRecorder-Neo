using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies local recording user settings synchronize before the first live control loop without blocking offline capture startup.</summary>
public sealed class RecordingConfigurationSynchronizationTests
{
    /// <summary>Startup applies a local edit once, even when the coordinator accepted it but its acknowledgement was lost.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunSynchronizesRelevantLocalSettingsWithoutRepeatingTheRevision(bool dropAcknowledgement)
    {
        using var fixture = new RecordingWorkerFixture();
        var configuration = fixture.Configuration with { UploadBytesPerSecond = 100 };
        await using var coordinator = new RecordingConfigurationTestCoordinator(new()
        {
            Configuration = configuration, ConfigurationRevision = 3, HeartbeatSeconds = 1
        }, dropAcknowledgement);
        var options = Connection(coordinator.Port);
        using var settings = new WorkerSettingsStore(fixture.SettingsPath, "Recording");
        var accepted = WorkerSettingsSynchronization.Accept(new() { Worker = options }, configuration, 3, "Recording");
        await settings.SaveAsync(accepted with { Configuration = configuration with { UploadBytesPerSecond = 200 } }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var running = new RecordingWorkerRuntime(options, settings, _ => { }).RunAsync(cancellation.Token);
        try
        {
            await coordinator.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var update = Assert.Single(coordinator.Hellos, hello => hello.InitialConfiguration is not null);
            Assert.Equal("Recording", update.Kind);
            Assert.Equal(3, update.ExpectedConfigurationRevision);
            Assert.Equal(200, update.InitialConfiguration!.UploadBytesPerSecond);
            Assert.Equal(4, settings.Current.ConfigurationRevision);
            Assert.Equal(200, settings.Current.Configuration.UploadBytesPerSecond);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Concurrent local and remote edits stop with a visible conflict and keep the original local settings untouched.</summary>
    [Fact]
    public async Task RunPreservesLocalSettingsWhenStartupRevisionConflicts()
    {
        using var fixture = new RecordingWorkerFixture();
        var configuration = fixture.Configuration with { UploadBytesPerSecond = 100 };
        await using var coordinator = new RecordingConfigurationTestCoordinator(new()
        {
            Configuration = configuration with { UploadBytesPerSecond = 300 }, ConfigurationRevision = 5
        });
        var options = Connection(coordinator.Port);
        using var settings = new WorkerSettingsStore(fixture.SettingsPath, "Recording");
        var accepted = WorkerSettingsSynchronization.Accept(new() { Worker = options }, configuration, 3, "Recording");
        await settings.SaveAsync(accepted with { Configuration = configuration with { UploadBytesPerSecond = 200 } }, CancellationToken.None);
        var before = await File.ReadAllTextAsync(fixture.SettingsPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var error = await Record.ExceptionAsync(() => new RecordingWorkerRuntime(options, settings, _ => { }).RunAsync(cancellation.Token));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.SettingsPath));
        Assert.Equal(200, settings.Current.Configuration.UploadBytesPerSecond);
        Assert.Single(coordinator.Hellos);
        Assert.False(coordinator.Heartbeat.Task.IsCompleted);
    }

    private static WorkerConnectionOptions Connection(int port) => new()
    {
        WorkerId = "worker", Secret = "secret", ApiBaseUrl = "http://127.0.0.1:1/",
        ControlHost = "127.0.0.1", ControlPort = port, ControlUseTls = false
    };
}
