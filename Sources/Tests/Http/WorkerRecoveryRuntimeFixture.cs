using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker;
using Yohuke.LiveRecorder.Clipping.Worker.Stages;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class WorkerRecoveryRuntimeFixture(string kind) : IDisposable
{
    internal string Kind { get; } = kind;
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "worker-recovery-runtime-" + Guid.NewGuid().ToString("N"));
    internal WorkerSettingsStore Settings { get; private set; } = null!;
    internal WorkerConnectionOptions Options { get; private set; } = null!;
    internal WorkerConfiguration Configuration { get; private set; } = null!;
    internal string CheckpointPath { get; private set; } = "";

    internal static async Task<WorkerRecoveryRuntimeFixture> CreateAsync(string kind, int controlPort, int jobs = 1)
    {
        var fixture = new WorkerRecoveryRuntimeFixture(kind);
        Directory.CreateDirectory(fixture.DirectoryPath);
        fixture.Configuration = new() { TempDirectory = fixture.DirectoryPath, MinimumFreeBytes = 0, MaxConcurrentEncodes = 1, MaxCacheBytes = 512L * 1024 * 1024 };
        fixture.Options = new()
        {
            ApiBaseUrl = "http://127.0.0.1:1", WorkerId = "worker", Secret = "secret", ControlUseTls = false,
            ControlHost = "127.0.0.1", ControlPort = controlPort
        };
        fixture.Settings = new(Path.Combine(fixture.DirectoryPath, "settings.json"), kind);
        await fixture.Settings.SaveAsync(new() { Worker = fixture.Options, Configuration = fixture.Configuration }, CancellationToken.None);
        for (var index = 0; index < jobs; index++)
        {
            var id = "job-" + index;
            if (kind == "Transcoding")
            {
                var store = new WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>(fixture.Settings.Path, "worker", "live-recorder-worker", _ => { });
                fixture.CheckpointPath = store.Open(fixture.DirectoryPath, id, new('a', 64), new() { JobId = id, Attempt = 1, LeaseToken = "lease" }).Path;
            }
            else
            {
                var store = new WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>(fixture.Settings.Path, "worker", "live-recorder-clipping-worker", _ => { });
                fixture.CheckpointPath = store.Open(fixture.DirectoryPath, id, new('a', 64), new() { JobId = id, Attempt = 1, LeaseToken = "lease" }).Path;
            }
        }
        return fixture;
    }

    internal Task RunAsync(CancellationToken cancellationToken) => Kind == "Transcoding"
        ? new WorkerRuntime(Options, Settings, _ => { }).RunAsync(cancellationToken)
        : new ClippingWorkerRuntime(Options, Settings, _ => { }).RunAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        Settings.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
