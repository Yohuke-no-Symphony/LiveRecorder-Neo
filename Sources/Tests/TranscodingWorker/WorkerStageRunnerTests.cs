using Xunit;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证已提交步骤恢复、产物失效和中断边界。</summary>
public sealed class WorkerStageRunnerTests
{
    /// <summary>重启只复用已完成且产物有效的步骤。</summary>
    [Fact]
    public async Task ResumesAtInterruptedStageWithoutRepeatingCompletedInput()
    {
        var snapshots = new List<WorkerStageSnapshot>();
        var savedStates = new List<string>();
        var context = new StageTestContext(snapshots, () => savedStates.Add(string.Join(',', snapshots.Select(value => value.State))));
        using var cancellation = new CancellationTokenSource();
        var first = new WorkerStageRunner<StageTestContext>([
            new TestWorkerStage("Download"),
            new TestWorkerStage("Encode", (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<WorkerStageResult>(cancellation.Token);
            })
        ]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(context, cancellation.Token));
        Assert.Equal("Completed", snapshots[0].State);
        Assert.Equal("Interrupted", snapshots[1].State);
        Assert.Contains("Completed,Running", savedStates);
        var resumed = new WorkerStageRunner<StageTestContext>([new TestWorkerStage("Download"), new TestWorkerStage("Encode")]);
        await resumed.RunAsync(context, CancellationToken.None);
        Assert.Equal(1, context.Executions["Download"]);
        Assert.Equal(2, context.Executions["Encode"]);
        Assert.All(snapshots, value => Assert.Equal("Completed", value.State));
    }

    /// <summary>缺失的上游产物使后续步骤失效，即使后续曾报告成功。</summary>
    [Fact]
    public async Task InvalidInputInvalidatesAllDownstreamStages()
    {
        var snapshots = new List<WorkerStageSnapshot>();
        var context = new StageTestContext(snapshots, () => { });
        var runner = new WorkerStageRunner<StageTestContext>([new TestWorkerStage("Download"), new TestWorkerStage("Encode")]);
        await runner.RunAsync(context, CancellationToken.None);
        context.ValidArtifacts.Remove("Download");
        await runner.RunAsync(context, CancellationToken.None);
        Assert.Equal(2, context.Executions["Download"]);
        Assert.Equal(2, context.Executions["Encode"]);
    }

    /// <summary>校验可以使编码及其后的步骤重新执行，而不重新下载。</summary>
    [Fact]
    public async Task ValidationRetriesFromExplicitEarlierBoundary()
    {
        var snapshots = new List<WorkerStageSnapshot>();
        var context = new StageTestContext(snapshots, () => { });
        var runner = new WorkerStageRunner<StageTestContext>([
            new TestWorkerStage("Download"), new TestWorkerStage("Encode"),
            new TestWorkerStage("Validate", (value, _) => Task.FromResult(
                value.Executions["Validate"] == 1 ? new WorkerStageResult("Encode") : WorkerStageResult.Completed))
        ]);
        await runner.RunAsync(context, CancellationToken.None);
        Assert.Equal(1, context.Executions["Download"]);
        Assert.Equal(2, context.Executions["Encode"]);
        Assert.Equal(2, context.Executions["Validate"]);
    }

    /// <summary>一个坏检查点不会阻断其它任务的恢复。</summary>
    [Fact]
    public void CorruptCheckpointIsQuarantinedWithoutDeletingArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker-stage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new WorkerCheckpointStore<TranscodeAssignment, TranscodeMediaInfo>(Path.Combine(directory, "settings.json"), "worker", "test-worker", _ => { });
            var valid = store.Open(directory, "valid", new('a', 64), new() { JobId = "valid" });
            var corrupt = store.Open(directory, "corrupt", new('b', 64), new() { JobId = "corrupt" });
            File.WriteAllText(corrupt.Path, "{");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(corrupt.Path)!, "source.flv"), "preserve");
            Assert.Equal(valid.Path, Assert.Single(store.ReadAll()).Path);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(corrupt.Path)!, "source.flv")));
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(corrupt.Path)!, "checkpoint.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
