using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Services;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>Verifies that different Worker executables share infrastructure without sharing system service state.</summary>
public sealed class WorkerServiceIdentityTests
{
    /// <summary>Each service definition keeps its own executable, owner, logs and native service identity.</summary>
    [Fact]
    public void DifferentWorkersProduceIndependentSystemServiceDefinitions()
    {
        var transcoding = WorkerServiceIdentity.Transcoding;
        var clipping = WorkerServiceIdentity.Clipping;
        var directory = Path.Combine(Path.GetTempPath(), "worker-identity-" + Guid.NewGuid().ToString("N"));
        var invocation = WorkerServiceInvocation.Create(Path.Combine(directory, "ylr-clip-worker"), null,
            Path.Combine(directory, "settings.json"));

        var unit = new LinuxWorkerService(clipping).CreateUnit(invocation);
        var plist = new MacWorkerService(clipping).CreatePlist(invocation);

        Assert.Contains(clipping.LinuxUnit, clipping.LinuxUnitPath);
        Assert.Contains("CacheDirectory=" + clipping.ServiceName, unit);
        Assert.Contains(clipping.LinuxBundleDirectory, unit);
        Assert.Contains(clipping.OwnerMarker, unit);
        Assert.Contains(clipping.DisplayName, unit);
        Assert.DoesNotContain(transcoding.OwnerMarker, unit);
        Assert.DoesNotContain(transcoding.ServiceName, unit);
        Assert.Contains(clipping.LaunchdLabel, plist);
        Assert.Contains(clipping.MacDefinitionDirectory + "/worker.log", plist);
        Assert.Contains(clipping.OwnerMarker, plist);
        Assert.DoesNotContain(transcoding.LaunchdLabel, plist);
        Assert.NotEqual(transcoding.MacDefinitionPath, clipping.MacDefinitionPath);
        Assert.NotEqual(transcoding.MacAutostartPath, clipping.MacAutostartPath);
        Assert.NotEqual(transcoding.WindowsRegistryPath, clipping.WindowsRegistryPath);

        var originalUnit = new LinuxWorkerService(transcoding).CreateUnit(invocation);
        Assert.Contains("CacheDirectory=yohuke-live-recorder-worker", originalUnit);
        Assert.Contains("Managed by Yohuke.LiveRecorder.Transcoding.Worker", originalUnit);
        Assert.Equal("/Library/Application Support/Yohuke.LiveRecorder/WorkerService", transcoding.MacDefinitionDirectory);
    }

    /// <summary>Both Worker types can inspect the same launchd output without selecting the other process.</summary>
    [Fact]
    public void LaunchdInspectionSelectsOnlyItsExplicitIdentity()
    {
        var output = "PID\tStatus\tLabel\n123\t0\tcom.yohuke.live-recorder.worker\n456\t0\tcom.yohuke.live-recorder.clipping-worker\n";

        Assert.Equal(123, new MacWorkerService(WorkerServiceIdentity.Transcoding).ParseState(output)?.ProcessId);
        Assert.Equal(456, new MacWorkerService(WorkerServiceIdentity.Clipping).ParseState(output)?.ProcessId);
        Assert.True(new MacWorkerInspection(WorkerServiceIdentity.Clipping).ParseDisabledState(
            "disabled services = {\n\"com.yohuke.live-recorder.worker\" => false\n\"com.yohuke.live-recorder.clipping-worker\" => true\n}"));
        Assert.False(new MacWorkerInspection(WorkerServiceIdentity.Transcoding).ParseDisabledState(
            "disabled services = {\n\"com.yohuke.live-recorder.worker\" => false\n\"com.yohuke.live-recorder.clipping-worker\" => true\n}"));
    }

    /// <summary>A clipping service operation rejects a managed definition owned by the transcoding Worker.</summary>
    [Fact]
    public async Task WorkerCannotManageAnotherKindsServiceDefinition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "service.definition");
            var content = "# " + WorkerServiceIdentity.Transcoding.OwnerMarker;
            await File.WriteAllTextAsync(path, content);

            Assert.Equal(content, await new WorkerServiceFiles(WorkerServiceIdentity.Transcoding).ReadManagedAsync(path, CancellationToken.None));
            await Assert.ThrowsAsync<WorkerServiceException>(() =>
                new WorkerServiceFiles(WorkerServiceIdentity.Clipping).ReadManagedAsync(path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Status and journal arguments follow the selected Worker rather than a process-wide service name.</summary>
    [Fact]
    public void LinuxInspectionUsesItsOwnServiceAndLogs()
    {
        var clipping = new LinuxWorkerInspection(WorkerServiceIdentity.Clipping);
        var transcoding = new LinuxWorkerInspection(WorkerServiceIdentity.Transcoding);
        var response = new WorkerProcessResult(0, "LoadState=loaded\nActiveState=active\nSubState=running\nMainPID=456\nUnitFileState=enabled\n", "");

        Assert.Contains("--unit=yohuke-live-recorder-clipping-worker.service", clipping.LogArguments);
        Assert.Contains("--unit=yohuke-live-recorder-worker.service", transcoding.LogArguments);
        Assert.Equal("journalctl --unit=yohuke-live-recorder-clipping-worker.service", clipping.ParseStatus(response).LogSource);
        Assert.Equal(456, clipping.ParseStatus(response).ProcessId);
    }

    /// <summary>Clipping settings synchronize using their own task kind without requiring transcoding GPU or CPU capacity.</summary>
    [Fact]
    public void ConfigurationSynchronizationPreservesWorkerKind()
    {
        var settings = new WorkerSettings
        {
            Worker = new() { WorkerId = "clip-worker", Secret = "clip-secret" },
            Configuration = new()
            {
                TempDirectory = Path.GetTempPath(),
                GpuConcurrency = 0,
                CpuConcurrency = 0
            }
        };

        var update = WorkerSettingsSynchronization.CreateUpdate(settings, new() { ConfigurationRevision = 3 }, "Clipping");

        Assert.NotNull(update);
        Assert.Equal("Clipping", update.Kind);
        Assert.Equal("clip-worker", update.WorkerId);
        Assert.Equal(3, update.ExpectedConfigurationRevision);
        Assert.Throws<ArgumentException>(() => WorkerSettingsSynchronization.CreateUpdate(settings, new() { ConfigurationRevision = 3 }));
    }
}
