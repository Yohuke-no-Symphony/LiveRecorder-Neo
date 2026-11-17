using System.Diagnostics;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>Exercises real process death, durable FLV recovery and child process containment.</summary>
public sealed class RecordingHardCrashTests
{
    /// <summary>A forcibly terminated recorder restarts offline, seals the durable prefix and continues the same broadcast.</summary>
    [Fact]
    public async Task ForcedProcessDeathResumesTheSameSessionWithoutCoordinator()
    {
        using var fixture = new RecordingWorkerFixture();
        var sample = Path.Combine(fixture.DirectoryPath, "sample.flv");
        var generated = await new WorkerProcessRunner().RunAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "30", "-c:v", "libx264",
            "-preset", "ultrafast", "-g", "10", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", sample
        ], CancellationToken.None);
        Assert.Equal(0, generated.ExitCode);
        using (var settings = new WorkerSettingsStore(fixture.SettingsPath, "Recording"))
        {
            await settings.SaveAsync(new()
            {
                Worker = new() { WorkerId = "worker", Secret = "offline-secret", ApiBaseUrl = "http://127.0.0.1:1/", ControlHost = "127.0.0.1", ControlPort = 1, ControlUseTls = false },
                Configuration = fixture.Configuration
            }, CancellationToken.None);
        }
        using (var spool = fixture.Spool())
        {
            spool.SaveAssignment(fixture.Assignment with { RecordDanmu = false }, fixture.DirectoryPath);
        }
        string sessionId;
        string sessionDirectory;
        string commitPath;
        MediaRecordingCommitSnapshot committed;
        await using (var first = new RecordingRecoveryTestProcess("record", fixture.SettingsPath, sample))
        {
            await first.WaitAsync(() => Directory.EnumerateFiles(fixture.DirectoryPath, "latest.json", SearchOption.AllDirectories)
                .Any(path => RecordingSpoolFiles.Read<MediaRecordingCommitSnapshot>(path).DurationMs > 0));
            first.Process.Kill(entireProcessTree: false);
            await first.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            commitPath = Directory.EnumerateFiles(fixture.DirectoryPath, "latest.json", SearchOption.AllDirectories).Single();
            committed = RecordingSpoolFiles.Read<MediaRecordingCommitSnapshot>(commitPath);
            Assert.False(committed.IsClosed);
            using var spool = fixture.Spool();
            var session = Assert.Single(spool.Sessions());
            sessionId = session.State.Descriptor.SessionId;
            sessionDirectory = session.DirectoryPath;
            Assert.False(session.State.Descriptor.IsClosed);
        }
        await using (var restarted = new RecordingRecoveryTestProcess("record", fixture.SettingsPath, sample))
        {
            await restarted.WaitAsync(() => Directory.EnumerateFiles(fixture.DirectoryPath, "latest.json", SearchOption.AllDirectories)
                .Any(path => path != commitPath && RecordingSpoolFiles.Read<MediaRecordingCommitSnapshot>(path).DurationMs > 0));
            var recovered = RecordingSpoolFiles.Read<MediaRecordingCommitSnapshot>(commitPath);
            Assert.True(recovered.IsClosed);
            Assert.Equal(committed.CommitSequence + 1, recovered.CommitSequence);
            Assert.Equal(committed.MediaCommittedBytes, new FileInfo(RecordingSpoolFiles.Resolve(sessionDirectory, recovered.RelativePath)).Length);
            var sessionPaths = Directory.EnumerateFiles(fixture.DirectoryPath, "session.json", SearchOption.AllDirectories).ToArray();
            Assert.Single(sessionPaths);
            Assert.Equal(sessionId, RecordingSpoolFiles.Read<RecordingSessionState>(sessionPaths[0]).Descriptor.SessionId);
        }
    }

    /// <summary>Killing only the Windows worker removes both direct and indirect children through the kernel job.</summary>
    [WindowsRecoveryFact]
    public async Task WindowsWorkerDeathTerminatesDescendants()
    {
        using var fixture = new RecordingWorkerFixture();
        var marker = Path.Combine(fixture.DirectoryPath, "process");
        await using var parent = new RecordingRecoveryTestProcess("job", marker);
        await parent.WaitAsync(() => File.Exists(marker + ".child") && File.Exists(marker + ".grandchild"));
        using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker + ".child")));
        using var grandchild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker + ".grandchild")));

        parent.Process.Kill(entireProcessTree: false);

        await Task.WhenAll(parent.Process.WaitForExitAsync(), child.WaitForExitAsync(), grandchild.WaitForExitAsync())
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Normal process return stays successful while the process-lifetime handle cleans up remaining descendants.</summary>
    [WindowsRecoveryFact]
    public async Task NormalWorkerExitDoesNotKillItselfBeforeReturningSuccess()
    {
        using var fixture = new RecordingWorkerFixture();
        var marker = Path.Combine(fixture.DirectoryPath, "process");
        await using var parent = new RecordingRecoveryTestProcess("job-exit", marker);

        await parent.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, parent.Process.ExitCode);
    }
}
