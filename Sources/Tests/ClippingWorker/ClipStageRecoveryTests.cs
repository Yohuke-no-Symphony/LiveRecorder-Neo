using System.Globalization;
using System.Net;
using Xunit;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker.Scheduling;
using Yohuke.LiveRecorder.Clipping.Worker.Stages;
using Yohuke.LiveRecorder.Clipping.Worker.Storage;
using Yohuke.LiveRecorder.Clipping.Worker.Transport;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Tests;

/// <summary>验证实际 FFmpeg 产物跨 JSON 检查点恢复及内容失效。</summary>
public sealed class ClipStageRecoveryTests
{
    /// <summary>上传中断后重启或重试复用导出产物，损坏成品只重做导出。</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task RestoresValidatedOutputWithoutDownloadingAgain(int nextAttempt, bool corruptOutput)
    {
        using var fixture = new ClipTestFixture();
        var (assignment, source) = await fixture.CreateAssignmentAsync("Exact");
        var downloads = 0;
        var completed = 0;
        var interrupt = true;
        using var interrupted = new CancellationTokenSource();
        using var handler = new ClipTestHttpHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                downloads++;
                var range = request.Headers.Range!.Ranges.Single();
                var start = checked((int)range.From!.Value);
                var end = checked((int)range.To!.Value);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(source[start..(end + 1)])
                };
                response.Headers.ETag = new("\"fixed-source\"");
                response.Content.Headers.ContentRange = new(start, end, source.Length);
                return response;
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload-session", StringComparison.Ordinal) && interrupt)
            {
                interrupt = false;
                interrupted.Cancel();
                throw new OperationCanceledException(interrupted.Token);
            }
            var result = new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Head)
            {
                result.Headers.Add("Upload-Offset", "0");
            }
            if (request.Method == HttpMethod.Put)
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                result.Headers.Add("Upload-Offset", bytes.Length.ToString(CultureInfo.InvariantCulture));
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                completed++;
            }
            return result;
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        var store = new WorkerCheckpointStore<ClipAssignment, ClipCheckpointPayload>(Path.Combine(fixture.Root, "settings.json"),
            fixture.Connection.WorkerId, "live-recorder-clipping-worker", _ => { });
        var location = store.Open(fixture.Root, assignment.JobId, ClipContentIdentity.Create(assignment), assignment);
        using (var workspace = new ClipWorkspace(fixture.Connection.WorkerId))
        using (var files = workspace.Acquire(fixture.Configuration, assignment, Path.GetDirectoryName(location.Path)))
        using (var job = new ClipRunningJob(assignment, CancellationToken.None))
        {
            var context = new ClipStageContext(location, files, fixture.Configuration, transfer, job, _ => { }, true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline().RunAsync(context, interrupted.Token));
        }
        var saved = Assert.Single(store.ReadAll());
        Assert.Equal("Completed", saved.Checkpoint.Stages.Single(stage => stage.Id == "Encoding").State);
        Assert.Equal("Interrupted", saved.Checkpoint.Stages.Single(stage => stage.Id == "Uploading").State);
        var previousOutput = saved.Checkpoint.Payload.OutputPath;
        if (corruptOutput)
        {
            var outputPath = WorkerArtifactStore.Resolve(Path.GetDirectoryName(saved.Path)!, previousOutput!);
            await using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Write);
            output.WriteByte(0xff);
        }
        var nextAssignment = assignment with { Attempt = nextAttempt, LeaseToken = "new-lease" };
        var resumed = store.Open(fixture.Root, assignment.JobId, ClipContentIdentity.Create(nextAssignment), nextAssignment);
        var logs = new List<string>();
        using (var workspace = new ClipWorkspace(fixture.Connection.WorkerId))
        using (var files = workspace.Acquire(fixture.Configuration, nextAssignment, Path.GetDirectoryName(resumed.Path)))
        using (var job = new ClipRunningJob(nextAssignment, CancellationToken.None))
        {
            var context = new ClipStageContext(resumed, files, fixture.Configuration, transfer, job, logs.Add, true);
            await Pipeline().RunAsync(context, CancellationToken.None);
        }
        Assert.Equal(1, downloads);
        Assert.Equal(1, completed);
        Assert.Equal(corruptOutput, logs.Contains("Exporting the clip."));
        Assert.Equal(!corruptOutput, previousOutput == resumed.Checkpoint.Payload.OutputPath);
        Assert.All(resumed.Checkpoint.Stages, stage => Assert.Equal("Completed", stage.State));
    }

    /// <summary>普通租约重试不改变内容身份，强制重建代次改变身份。</summary>
    [Fact]
    public void FingerprintSeparatesForceFromLeaseAndRetryIdentity()
    {
        var assignment = ClipTestFixture.SmallAssignment();
        var fingerprint = ClipContentIdentity.Create(assignment);
        Assert.Equal(fingerprint, ClipContentIdentity.Create(assignment with { Attempt = 12, LeaseToken = "replacement" }));
        Assert.NotEqual(fingerprint, ClipContentIdentity.Create(assignment with { CheckpointGeneration = 1 }));
    }

    private static WorkerStageRunner<ClipStageContext> Pipeline() => new([
        new ClipDownloadStage(), new ClipPrepareStage(), new ClipExportStage(), new ClipValidateStage(), new ClipUploadStage(), new ClipFinalizeStage()
    ]);
}
