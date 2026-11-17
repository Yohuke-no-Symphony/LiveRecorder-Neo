using System.Globalization;
using System.Net;
using Xunit;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Yohuke.LiveRecorder.Transcoding.Worker.Stages;
using Yohuke.LiveRecorder.Transcoding.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;
using Yohuke.LiveRecorder.Worker.Common.Stages;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证真实 HLS 成品在上传中断后通过持久检查点续跑。</summary>
public sealed class TranscodeStageRecoveryTests
{
    /// <summary>恢复上传不重新获取编码资源或运行编码，并保留完整校验 manifest。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartsAtUploadWithoutReencodingValidatedHls(bool corruptSource)
    {
        var directory = Path.Combine(Path.GetTempPath(), "transcode-stage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var processes = new WorkerProcessRunner();
            var sample = Path.Combine(directory, "sample.flv");
            var generated = await processes.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10",
                "-t", "8", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", "-g", "20", "-pix_fmt", "yuv420p", sample], CancellationToken.None);
            Assert.True(generated.ExitCode == 0, generated.Diagnostic);
            var source = await File.ReadAllBytesAsync(sample);
            var configuration = new WorkerConfiguration { TempDirectory = directory, MinimumFreeBytes = 0, MaxCacheBytes = 256L * 1024 * 1024 };
            var assignment = new TranscodeAssignment
            {
                JobId = "job", Attempt = 1, LeaseToken = "lease", AssetId = "source", SourceVersion = "source-v1",
                SourceETag = "\"source-v1\"", SourceLength = source.Length,
                Profile = new() { Height = 64, VideoBitrateKbps = 64, SegmentDurationSeconds = 2, KeyframeIntervalSeconds = 2 }
            };
            var downloads = 0;
            var reservations = 0;
            var completed = 0;
            var interrupt = true;
            var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
            using var interrupted = new CancellationTokenSource();
            using var handler = new WorkerTestHttpHandler(async (request, cancellationToken) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/input", StringComparison.Ordinal))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(source) };
                    response.Headers.ETag = new("\"source-v1\"");
                    if (request.Method == HttpMethod.Get)
                    {
                        downloads++;
                    }
                    return response;
                }
                if (path.EndsWith("/upload-session", StringComparison.Ordinal) && interrupt)
                {
                    interrupt = false;
                    interrupted.Cancel();
                    throw new OperationCanceledException(interrupted.Token);
                }
                var result = new HttpResponseMessage(HttpStatusCode.NoContent);
                if (request.Method == HttpMethod.Head)
                {
                    result.Headers.Add("Upload-Offset", offsets.GetValueOrDefault(path).ToString(CultureInfo.InvariantCulture));
                }
                if (request.Method == HttpMethod.Put)
                {
                    var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    offsets[path] = offsets.GetValueOrDefault(path) + bytes.Length;
                    result.Headers.Add("Upload-Offset", offsets[path].ToString(CultureInfo.InvariantCulture));
                }
                if (path.EndsWith("/complete", StringComparison.Ordinal))
                {
                    completed++;
                }
                return result;
            });
            using var transfer = new WorkerFileTransfer(new() { ApiBaseUrl = "https://example.invalid", WorkerId = "worker", Secret = "secret" }, handler);
            var inspector = new MediaInspector(processes);
            var encoder = new TranscodeEncoder(processes, _ => { });
            var store = new WorkerCheckpointStore<TranscodeAssignment, TranscodeCheckpointPayload>(Path.Combine(directory, "settings.json"),
                "worker", "live-recorder-worker", _ => { });
            var location = store.Open(directory, assignment.JobId, TranscodeContentIdentity.Create(assignment), assignment);
            Task<(WorkerRuntimeConfiguration Configuration, EncodingReservation Reservation)> Reserve(CancellationToken _)
            {
                reservations++;
                return Task.FromResult((new WorkerRuntimeConfiguration(configuration, new(), 1),
                    new EncodingReservation(new() { Encoder = "libx264", Available = true }, 1, () => { })));
            }
            using (var workspace = new WorkerWorkspace("worker"))
            using (var files = workspace.Acquire(configuration, assignment, checkpointDirectory: Path.GetDirectoryName(location.Path)))
            using (var job = new WorkerRunningJob(assignment, CancellationToken.None))
            {
                var context = new TranscodeStageContext(location, files, configuration, transfer, inspector, encoder,
                    job, new(job, _ => { }), Reserve, true);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline().RunAsync(context, interrupted.Token));
            }
            var before = reservations;
            var saved = Assert.Single(store.ReadAll());
            var originalOutput = saved.Checkpoint.Payload.OutputDirectory;
            Assert.NotNull(saved.Checkpoint.Payload.Manifest);
            Assert.Equal("Interrupted", saved.Checkpoint.Stages.Single(stage => stage.Id == "Uploading").State);
            if (corruptSource)
            {
                var sourcePath = Directory.EnumerateFiles(Path.Combine(directory, "live-recorder-worker", "worker", "inputs"),
                    "source.flv", SearchOption.AllDirectories).Single();
                await using var damaged = new FileStream(sourcePath, FileMode.Open, FileAccess.Write);
                damaged.WriteByte(0xff);
            }
            var next = assignment with { Attempt = 2, LeaseToken = "next-lease" };
            var resumed = store.Open(directory, next.JobId, TranscodeContentIdentity.Create(next), next);
            using (var workspace = new WorkerWorkspace("worker"))
            using (var files = workspace.Acquire(configuration, next, checkpointDirectory: Path.GetDirectoryName(resumed.Path)))
            using (var job = new WorkerRunningJob(next, CancellationToken.None))
            {
                var context = new TranscodeStageContext(resumed, files, configuration, transfer, inspector, encoder,
                    job, new(job, _ => { }), Reserve, true);
                await Pipeline().RunAsync(context, CancellationToken.None);
            }
            Assert.Equal(corruptSource ? 2 : 1, downloads);
            Assert.Equal(corruptSource ? before + 1 : before, reservations);
            Assert.Equal(!corruptSource, originalOutput == resumed.Checkpoint.Payload.OutputDirectory);
            Assert.Equal(1, completed);
            Assert.All(resumed.Checkpoint.Stages, stage => Assert.Equal("Completed", stage.State));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>强制重建与源配方改变均使旧成品检查点失效。</summary>
    [Fact]
    public void ContentFingerprintSeparatesSourceRecipeAndForce()
    {
        var assignment = new TranscodeAssignment { SourceVersion = "v1", RecipeHash = "recipe" };
        var fingerprint = TranscodeContentIdentity.Create(assignment);
        Assert.Equal(fingerprint, TranscodeContentIdentity.Create(assignment with { Attempt = 2, LeaseToken = "new" }));
        Assert.NotEqual(fingerprint, TranscodeContentIdentity.Create(assignment with { SourceVersion = "v2" }));
        Assert.NotEqual(fingerprint, TranscodeContentIdentity.Create(assignment with { RecipeHash = "new" }));
        Assert.NotEqual(fingerprint, TranscodeContentIdentity.Create(assignment with { CheckpointGeneration = 1 }));
    }

    private static WorkerStageRunner<TranscodeStageContext> Pipeline() => new([
        new TranscodeDownloadStage(), new TranscodeInspectStage(), new TranscodeEncodeStage(), new TranscodeValidateStage(),
        new TranscodeUploadStage(), new TranscodeFinalizeStage()
    ]);
}
