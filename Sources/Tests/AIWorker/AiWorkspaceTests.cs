using Xunit;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.AI.Worker.Storage;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

public sealed class AiWorkspaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ai-workspace-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ChangingTemporaryRootKeepsPreviousUnfinishedCheckpointsDiscoverable()
    {
        var settings = Path.Combine(directory, "settings.json");
        var workspace = new AiWorkspace(settings, "worker", _ => { });
        var first = workspace.Open(Configuration("first"), new() { JobId = "job", Attempt = 1, InputFingerprint = "fingerprint" });
        first.Update(value => value.State = "Paused");
        workspace.RegisterRoot(Configuration("second").TempDirectory);

        var recovered = Assert.Single(new AiWorkspace(settings, "worker", _ => { }).ReadPending());

        Assert.Equal(first.Path, recovered.Path);
        Assert.Equal("fingerprint", recovered.Read().Assignment.InputFingerprint);
    }

    [Fact]
    public void CapacityEvictsCompletedCacheAndRetainsPausedUnknownRequests()
    {
        var workspace = new AiWorkspace(Path.Combine(directory, "settings.json"), "worker", _ => { });
        var configuration = Configuration("cache");
        var completed = workspace.Open(configuration, new() { JobId = "completed", Attempt = 1 });
        completed.Update(value => value.State = "Completed");
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(completed.Path)!, "old-input"), new byte[8192]);
        var paused = workspace.Open(configuration, new() { JobId = "unknown", Attempt = 1 });
        paused.Update(value =>
        {
            value.State = "Paused";
            value.Journal = new() { Dispatched = true, RequestNumber = 1 };
        });

        workspace.EnsureSpace(configuration with { MaxCacheBytes = 8192 }, 1024);

        Assert.False(File.Exists(completed.Path));
        Assert.True(File.Exists(paused.Path));
        Assert.Throws<IOException>(() => workspace.EnsureSpace(configuration with { MaxCacheBytes = 1 }, 1));
        Assert.True(File.Exists(paused.Path));
    }

    [Fact]
    public void SourceIdentityCannotEscapeInputDirectory()
    {
        var path = AiWorkspace.SourcePath(directory, "../../external/secret");
        Assert.Equal(Path.Combine(directory, "inputs"), Path.GetDirectoryName(path));
        Assert.DoesNotContain("secret", Path.GetFileName(path));
    }

    private AiWorkerConfiguration Configuration(string name) => new()
    {
        TempDirectory = Path.Combine(directory, name), MaxCacheBytes = 1024 * 1024, MinimumFreeBytes = 0
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
