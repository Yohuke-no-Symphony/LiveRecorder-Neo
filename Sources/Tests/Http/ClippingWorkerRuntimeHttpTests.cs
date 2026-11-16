using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Clipping.Worker;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>Runs the actual clipping runtime against TCP control and HTTP media endpoints.</summary>
public sealed class ClippingWorkerRuntimeHttpTests
{
    /// <summary>An exact clipping node connects over TLS, publishes the clip and removes all task media.</summary>
    [MongoHttpFact]
    public Task ExactWorkerCompletesThroughTlsControlAndHttps() => RunAsync("Exact", true);

    /// <summary>A fast clipping node receives prealigned bounds and publishes through the same shared control service.</summary>
    [MongoHttpFact]
    public Task FastWorkerCompletesThroughTcpControlAndHttp() => RunAsync("Fast", false);

    /// <summary>The standalone command synchronizes its settings and executes a real remote task.</summary>
    [MongoHttpFact]
    public Task StandaloneRunCommandCompletesRemoteTask() => RunAsync("Exact", false, true);

    private static async Task RunAsync(string mode, bool tls, bool standalone = false)
    {
        using var certificate = new TranscodingTestCertificate();
        var arguments = new[]
        {
            "--TranscodingControl:Enabled", "true",
            "--TranscodingControl:ListenAddress", "127.0.0.1", "--TranscodingControl:Port", certificate.ControlPort.ToString(),
            "--TranscodingControl:UseTls", tls ? "true" : "false", "--TranscodingControl:CertificatePath", certificate.Path
        };
        await using var fixture = tls
            ? await HttpServerFixture.CreateHttpsAsync(certificate.Path, certificate.Sha256, arguments)
            : await HttpServerFixture.CreateAsync("Production", arguments);
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        foreach (var service in fixture.App.Services.GetServices<IHostedService>().Where(service => service is MediaJobWorker or RecordingSupervisor))
        {
            await service.StopAsync(CancellationToken.None);
        }
        var globalSettings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        globalSettings!["minimumFreeBytes"] = 0;
        using (var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", globalSettings))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 8);
        using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers",
            new { name = "Actual clipping runtime", kind = "Clipping" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var worker = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = worker["worker"]!["id"]!.GetValue<string>();
        var secret = worker["secret"]!.GetValue<string>();
        var nodeDirectory = Path.Combine(fixture.DirectoryPath, "clipping-node");
        var configuration = new WorkerConfiguration
        {
            TempDirectory = nodeDirectory, MinimumFreeBytes = 0, MaxCacheBytes = 512L * 1024 * 1024,
            MaxConcurrentEncodes = 1
        };
        using (var configured = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + id,
            new { name = "Actual clipping runtime", enabled = true, configuration, expectedRevision = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        var options = new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri, ControlHost = "127.0.0.1", ControlPort = certificate.ControlPort,
            ControlUseTls = tls, WorkerId = id, Secret = secret,
            ServerCertificateSha256 = tls ? certificate.Sha256 : null
        };
        using var settings = new WorkerSettingsStore(Path.Combine(fixture.DirectoryPath, "node-settings.json"), "Clipping");
        await settings.SaveAsync(new() { Worker = options, Configuration = configuration }, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var logs = new ConcurrentQueue<string>();
        if (standalone)
        {
            settings.Dispose();
        }
        var running = standalone ? RunCommandAsync(settings.Path, lifetime.Token, logs)
            : new ClippingWorkerRuntime(options, settings, logs.Enqueue).RunAsync(lifetime.Token);
        try
        {
            using var queued = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/jobs", new
            {
                kind = "Clip", mode, sessionId = session.Id, startMs = 3500, endMs = 4250, title = "Actual remote clipping"
            });
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            var queuedJob = (await queued.Content.ReadFromJsonAsync<MediaJob>())!;
            MediaJob? job;
            while (true)
            {
                if (running.IsCompleted)
                {
                    await running;
                    Assert.Fail("Clipping runtime stopped before completing the task.");
                }
                job = await fixture.Context.Collection<MediaJob>("jobs").Find(value => value.Id == queuedJob.Id).SingleAsync(timeout.Token);
                if (job.State is "Completed" or "Failed" or "Cancelled")
                {
                    break;
                }
                Assert.True(!timeout.IsCancellationRequested, string.Join('\n', logs));
                await Task.Delay(100, timeout.Token);
            }
            Assert.True(job.State == "Completed", job.Error + "\n" + string.Join('\n', logs));
            Assert.Equal(id, job.RemoteWorkerId);
            Assert.NotNull(job.RemoteAssignment);
            var range = Assert.Single(job.RemoteAssignment.Inputs).Range!;
            Assert.True(range.EndOffset - range.StartOffset < range.SourceLength / 2);
            var clip = await fixture.Context.Collection<Clip>("clips").Find(value => value.JobId == job.Id).SingleAsync(timeout.Token);
            Assert.Equal(job.RemoteAssignment.ActualStartMs, clip.StartMs);
            Assert.Equal(job.RemoteAssignment.ActualEndMs, clip.EndMs);
            if (mode == "Fast")
            {
                Assert.True(clip.StartMs <= 3500);
                Assert.True(clip.EndMs >= 4250);
            }
            else
            {
                Assert.Equal(3500, clip.StartMs);
                Assert.Equal(4250, clip.EndMs);
            }
            using var download = await fixture.Admin.Client.GetAsync("/api/clips/" + clip.Id + "/download", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(clip.Bytes, (await download.Content.ReadAsByteArrayAsync(timeout.Token)).LongLength);
            var taskDirectory = Path.Combine(nodeDirectory, "live-recorder-clipping-worker", id, "jobs");
            while (Directory.Exists(taskDirectory) && Directory.EnumerateDirectories(taskDirectory).Any())
            {
                await Task.Delay(50, timeout.Token);
            }
            Assert.False(Directory.Exists(Path.Combine(fixture.StoragePath, ".work", "jobs", job.Id, job.Attempt.ToString())));
            Assert.Empty(Directory.EnumerateFiles(nodeDirectory, "*.flv", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(nodeDirectory, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task RunCommandAsync(string path, CancellationToken cancellationToken, ConcurrentQueue<string> logs)
    {
        var result = await new WorkerProcessRunner().RunAsync("dotnet",
            [typeof(ClippingWorkerRuntime).Assembly.Location, "run", "--config", path], cancellationToken,
            outputLine: logs.Enqueue, errorLine: logs.Enqueue);
        Assert.True(result.ExitCode == 0, result.Diagnostic);
    }
}
