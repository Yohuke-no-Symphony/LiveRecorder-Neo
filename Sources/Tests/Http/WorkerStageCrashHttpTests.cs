using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker;
using Yohuke.LiveRecorder.Clipping.Worker.Stages;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过独立 worker 进程强杀验证完整控制、检查点、恢复和发布链路。</summary>
public sealed class WorkerStageCrashHttpTests
{
    /// <summary>上传阶段强杀后相同配置重启，保留 Attempt 并换取新租约，复用原导出成品。</summary>
    [MongoHttpFact]
    public async Task KilledClippingProcessResumesItsValidatedOutput()
    {
        using var certificate = new TranscodingTestCertificate();
        await using var fixture = await HttpServerFixture.CreateAsync("Production",
            "--TranscodingControl:Enabled", "true", "--TranscodingControl:ListenAddress", "127.0.0.1",
            "--TranscodingControl:Port", certificate.ControlPort.ToString(), "--TranscodingControl:UseTls", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        foreach (var service in fixture.App.Services.GetServices<IHostedService>().Where(value => value is MediaJobWorker or RecordingSupervisor))
        {
            await service.StopAsync(CancellationToken.None);
        }
        var global = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        global["minimumFreeBytes"] = 0;
        using (var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", global))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var session = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 8);
        using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = "Crash recovery clipping", kind = "Clipping" });
        var registration = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var workerId = registration["worker"]!["id"]!.GetValue<string>();
        var options = new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri, WorkerId = workerId, Secret = registration["secret"]!.GetValue<string>(),
            ControlHost = "127.0.0.1", ControlPort = certificate.ControlPort, ControlUseTls = false
        };
        var configuration = new WorkerConfiguration
        {
            TempDirectory = Path.Combine(fixture.DirectoryPath, "crash-node"), MinimumFreeBytes = 0,
            MaxCacheBytes = 512L * 1024 * 1024, MaxConcurrentEncodes = 1, UploadBytesPerSecond = 1024
        };
        var revision = await ConfigureAsync(fixture, workerId, configuration, 1);
        var settingsPath = Path.Combine(fixture.DirectoryPath, "crash-node-settings.json");
        using (var settings = new WorkerSettingsStore(settingsPath, "Clipping"))
        {
            await settings.SaveAsync(new() { Worker = options, Configuration = configuration }, CancellationToken.None);
        }
        var firstLogs = new ConcurrentQueue<string>();
        var secondLogs = new ConcurrentQueue<string>();
        using var first = StartWorker(settingsPath, firstLogs);
        Process? second = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using var queued = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/jobs", new
            {
                kind = "Clip", mode = "Exact", sessionId = session.Id, startMs = 1500, endMs = 6500, title = "Crash stage recovery"
            });
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            var jobId = (await queued.Content.ReadFromJsonAsync<MediaJob>())!.Id;
            var jobsDirectory = Path.Combine(configuration.TempDirectory, "live-recorder-clipping-worker", workerId, "jobs");
            WorkerJobCheckpoint<ClipAssignment, ClipCheckpointPayload>? interrupted = null;
            while (interrupted is null)
            {
                Assert.False(first.HasExited, string.Join('\n', firstLogs));
                if (Directory.Exists(jobsDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(jobsDirectory, "checkpoint.json", SearchOption.AllDirectories))
                    {
                        var checkpoint = WorkerJsonFileStore.Read<WorkerJobCheckpoint<ClipAssignment, ClipCheckpointPayload>>(path);
                        if (checkpoint.JobId == jobId && checkpoint.Stages.Any(stage => stage.Id == "Uploading" && stage.State == "Running"))
                        {
                            interrupted = checkpoint;
                            break;
                        }
                    }
                }
                if (interrupted is null)
                {
                    await Task.Delay(20, timeout.Token);
                }
            }
            Assert.NotNull(interrupted.Payload.Manifest);
            Assert.Equal("Completed", interrupted.Stages.Single(stage => stage.Id == "Encoding").State);
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(timeout.Token);
            _ = await ConfigureAsync(fixture, workerId, configuration with { UploadBytesPerSecond = 0 }, revision);
            second = StartWorker(settingsPath, secondLogs);
            MediaJob? finished = null;
            while (finished is null)
            {
                Assert.False(second.HasExited, string.Join('\n', secondLogs));
                var current = await fixture.Context.Collection<MediaJob>("jobs").Find(value => value.Id == jobId).SingleAsync(timeout.Token);
                Assert.True(current.State is not ("Failed" or "Cancelled"), current.Error + "\n" + string.Join('\n', secondLogs));
                if (current.State == "Completed"
                    && await fixture.Context.Collection<Clip>("clips").Find(value => value.JobId == jobId && !value.PublicationPending).AnyAsync(timeout.Token))
                {
                    finished = current;
                }
                else
                {
                    await Task.Delay(50, timeout.Token);
                }
            }
            Assert.Equal(interrupted.Assignment.Attempt, finished.Attempt);
            Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(interrupted.Assignment.LeaseToken))), finished.RemoteLeaseTokenHash);
            Assert.DoesNotContain(secondLogs, line => line.Contains("Exporting the clip.", StringComparison.Ordinal));
            Assert.DoesNotContain(secondLogs, line => line.Contains("Downloading the assigned clipping ranges.", StringComparison.Ordinal));
            var clip = await fixture.Context.Collection<Clip>("clips").Find(value => value.JobId == jobId && !value.PublicationPending).SingleAsync(timeout.Token);
            using var download = await fixture.Admin.Client.GetAsync("/api/clips/" + clip.Id + "/download", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(interrupted.Payload.Manifest.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(await download.Content.ReadAsByteArrayAsync(timeout.Token))));
            while (Directory.Exists(jobsDirectory) && Directory.EnumerateDirectories(jobsDirectory).Any())
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            await StopWorkerAsync(first);
            if (second is not null)
            {
                await StopWorkerAsync(second);
                second.Dispose();
            }
        }
    }

    private static async Task<int> ConfigureAsync(HttpServerFixture fixture, string workerId, WorkerConfiguration configuration, int revision)
    {
        using var configured = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + workerId,
            new { name = "Crash recovery clipping", enabled = true, configuration, expectedRevision = revision });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        return (await configured.Content.ReadFromJsonAsync<JsonObject>())!["configurationRevision"]!.GetValue<int>();
    }

    private static Process StartWorker(string settingsPath, ConcurrentQueue<string> logs)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { typeof(ClippingWorkerRuntime).Assembly.Location, "run", "--config", settingsPath })
        {
            start.ArgumentList.Add(argument);
        }
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
            {
                logs.Enqueue(line);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
            {
                logs.Enqueue(line);
            }
        };
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task StopWorkerAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
