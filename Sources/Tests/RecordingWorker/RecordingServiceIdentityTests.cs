using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Services;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Verifies the recording executable can coexist with both existing Worker service installations.</summary>
public sealed class RecordingServiceIdentityTests
{
    /// <summary>Native service definitions and paths remain specific to recording on every supported service manager.</summary>
    [Fact]
    public void RecordingUsesAnIndependentServiceIdentity()
    {
        var identity = WorkerServiceIdentity.Recording;
        var directory = Path.Combine(Path.GetTempPath(), "recording-service");
        var invocation = WorkerServiceInvocation.Create(Path.Combine(directory, "ylr-rec-worker"), null, Path.Combine(directory, "settings.json"));
        var unit = new LinuxWorkerService(identity).CreateUnit(invocation);
        var plist = new MacWorkerService(identity).CreatePlist(invocation);

        Assert.Contains("CacheDirectory=yohuke-live-recorder-recording-worker", unit);
        Assert.Contains("Managed by Yohuke.LiveRecorder.Recording.Worker", unit);
        Assert.Contains("com.yohuke.live-recorder.recording-worker", plist);
        Assert.Contains("RecordingWorkerService/worker.log", plist);
        foreach (var other in new[] { WorkerServiceIdentity.Transcoding, WorkerServiceIdentity.Clipping })
        {
            Assert.DoesNotContain(other.OwnerMarker, unit);
            Assert.NotEqual(other.LinuxUnitPath, identity.LinuxUnitPath);
            Assert.NotEqual(other.MacDefinitionPath, identity.MacDefinitionPath);
            Assert.NotEqual(other.MacAutostartPath, identity.MacAutostartPath);
            Assert.NotEqual(other.WindowsRegistryPath, identity.WindowsRegistryPath);
        }
    }

    /// <summary>Recording synchronizes its task kind and persistent storage configuration without media tool requirements.</summary>
    [Fact]
    public void RecordingConfigurationDoesNotRequireTranscodingTools()
    {
        var settings = new WorkerSettings
        {
            Worker = new() { WorkerId = "recorder", Secret = "secret" },
            Configuration = new()
            {
                TempDirectory = Path.GetTempPath(), FfmpegPath = "", FfprobePath = "", GpuConcurrency = 0, CpuConcurrency = 0
            }
        };

        var update = WorkerSettingsSynchronization.CreateUpdate(settings, new() { ConfigurationRevision = 3 }, "Recording");

        Assert.NotNull(update);
        Assert.Equal("Recording", update.Kind);
        Assert.Equal(3, update.ExpectedConfigurationRevision);
        Assert.Equal(settings.Configuration, update.InitialConfiguration);
    }
}
