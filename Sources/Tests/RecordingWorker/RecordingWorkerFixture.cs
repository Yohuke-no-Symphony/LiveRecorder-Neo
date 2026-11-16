using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingWorkerFixture : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "recording-worker-tests", Guid.NewGuid().ToString("N"));
    internal string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    internal WorkerConfiguration Configuration { get; }
    internal RecordingAssignment Assignment { get; } = new()
    {
        AssignmentId = "assignment", Generation = 1, Token = "assignment-token", WorkerId = "worker",
        TargetId = "target", RoomId = 123, RecordDanmu = true, MonitorIntervalSeconds = 1
    };

    internal RecordingWorkerFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        Configuration = new() { TempDirectory = DirectoryPath, MinimumFreeBytes = 0, MaxCacheBytes = 64L * 1024 * 1024 };
    }

    internal RecordingSpool Spool() => new(SettingsPath, "worker", DirectoryPath, _ => { });

    internal RecordingSpoolSession Session(RecordingSpool spool) => spool.CreateSession(Assignment, new()
    {
        SessionId = "session", RoomId = 123, StartedAtUtc = DateTime.UtcNow, Title = "Test broadcast"
    }, DirectoryPath);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
