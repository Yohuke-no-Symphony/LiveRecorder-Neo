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

    /// <summary>损坏检查点不会阻断后续回收，空间不足时仍保留未完成任务并报告容量错误。</summary>
    [Theory]
    [InlineData(true, "Completed", "{private-checkpoint-body")]
    [InlineData(false, "Completed", "{private-checkpoint-body")]
    [InlineData(true, "Rejected", "{private-checkpoint-body")]
    [InlineData(false, "Rejected", "{private-checkpoint-body")]
    [InlineData(true, "Completed", "null")]
    [InlineData(false, "Completed", "null")]
    [InlineData(true, "Rejected", "null")]
    [InlineData(false, "Rejected", "null")]
    public void CapacitySkipsUnreadableCheckpointsAndPreservesUnfinishedTasks(bool sufficientSpace, string terminalState, string corruptJson)
    {
        var warnings = new List<string>();
        var workspace = new AiWorkspace(Path.Combine(directory, "settings.json"), "worker", warnings.Add);
        var configuration = Configuration("corrupt-cache");
        var corrupt = workspace.Open(configuration, new() { JobId = "corrupt", Attempt = 1 });
        var completed = workspace.Open(configuration, new() { JobId = "terminal", Attempt = 1 });
        completed.Update(value => value.State = terminalState);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(completed.Path)!, "old-input"), new byte[8192]);
        var paused = workspace.Open(configuration, new() { JobId = "paused", Attempt = 1 });
        paused.Update(value => value.State = "Paused");
        var unknown = workspace.Open(configuration, new() { JobId = "unknown", Attempt = 1 });
        unknown.Update(value =>
        {
            value.State = "Paused";
            value.Journal = new() { Dispatched = true, RequestNumber = 1 };
        });
        File.WriteAllText(corrupt.Path, corruptJson);
        var retained = new[] { corrupt.Path, paused.Path, unknown.Path }.ToDictionary(path => path, File.ReadAllBytes);
        var retainedBytes = retained.Values.Sum(bytes => (long)bytes.Length);
        var constrained = configuration with { MaxCacheBytes = sufficientSpace ? retainedBytes + 128 : retainedBytes - 1 };
        var timestamp = DateTime.UtcNow.AddMinutes(-10);
        var paths = new[] { corrupt.Path, completed.Path, paused.Path, unknown.Path };
        for (var index = 0; index < paths.Length; index++)
        {
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(paths[index])!, timestamp.AddMinutes(index));
        }
        Assert.Equal(2, workspace.ReadPending().Count);
        warnings.Clear();

        if (sufficientSpace)
        {
            workspace.EnsureSpace(constrained, 0);
        }
        else
        {
            var error = Assert.Throws<IOException>(() => workspace.EnsureSpace(constrained, 0));
            Assert.Contains("capacity is unavailable", error.Message);
        }

        Assert.False(Directory.Exists(Path.GetDirectoryName(completed.Path)));
        foreach (var entry in retained)
        {
            Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
        }
        Assert.Contains(warnings, message => message.Contains("unreadable AI checkpoint", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, message => message.Contains("private-checkpoint-body", StringComparison.Ordinal));
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
