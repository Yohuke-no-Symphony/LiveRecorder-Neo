using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Recording.Worker;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>Verifies that a real recording runtime uploads playable media through the shared control service.</summary>
public sealed class RecordingWorkerRuntimeHttpTests
{
    /// <summary>The node uploads media, index and events over HTTP while its source remains open.</summary>
    [MongoHttpFact]
    public Task UploadedRecordingPlaysBeforeCaptureStops() => RunAsync(tls: false);

    /// <summary>The same recording flow supports pinned TLS control and HTTPS transfers.</summary>
    [MongoHttpFact]
    public Task RecordingSupportsPinnedTlsControlAndHttps() => RunAsync(tls: true);

    /// <summary>A restarted Worker recovers its durable assignment and continues the existing session.</summary>
    [MongoHttpFact]
    public Task WorkerRestartResumesTheExistingSession() => RunAsync(tls: false, restartWorker: true);

    /// <summary>One node captures and uploads two independent rooms within its configured capacity.</summary>
    [MongoHttpFact]
    public Task OneWorkerRecordsTwoRoomsConcurrently() => RunAsync(tls: false, roomCount: 2);

    /// <summary>Running after editing a valid local recording setting synchronizes it before applying server state.</summary>
    [MongoHttpFact]
    public Task RecordingRunSynchronizesValidLocalConfiguration() => RunAsync(tls: false, localOverride: true);

    /// <summary>Restarting the main server does not stop capture, transfer room ownership or lose queued media.</summary>
    [MongoHttpFact]
    public async Task MainServerRestartRetainsOwnershipAndResumesUploading()
    {
        using var control = new TranscodingTestCertificate();
        using var apiPort = new TranscodingTestCertificate();
        await using var fixture = await HttpServerFixture.CreateAsync("Production",
            "--urls", $"http://127.0.0.1:{apiPort.ControlPort}",
            "--TranscodingControl:Enabled", "true", "--TranscodingControl:ListenAddress", "127.0.0.1",
            "--TranscodingControl:Port", control.ControlPort.ToString(), "--TranscodingControl:UseTls", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var sample = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 20);
        var sourceSegment = await fixture.Context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == sample.Id).SingleAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, sample.DirectoryPath, sourceSegment.RelativePath));
        using var handler = new RecordingLiveTestHttpHandler(bytes, TimeSpan.FromMilliseconds(50));
        using var sourceClient = new HttpClient(handler, disposeHandler: false);
        using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = "Restart-safe recorder", kind = "Recording" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var registration = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var workerId = registration["worker"]!["id"]!.GetValue<string>();
        var configuration = new WorkerConfiguration
        {
            TempDirectory = Path.Combine(fixture.DirectoryPath, "offline-recording-node"), MinimumFreeBytes = 0,
            MaxCacheBytes = 512L * 1024 * 1024, MaxConcurrentEncodes = 1
        };
        using (var configured = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + workerId,
            new { name = "Restart-safe recorder", enabled = true, configuration, expectedRevision = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        var options = new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri, ControlHost = "127.0.0.1", ControlPort = control.ControlPort,
            ControlUseTls = false, WorkerId = workerId, Secret = registration["secret"]!.GetValue<string>()
        };
        using var settings = new WorkerSettingsStore(Path.Combine(fixture.DirectoryPath, "offline-recording-settings.json"), "Recording");
        await settings.SaveAsync(new() { Worker = options, Configuration = configuration }, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var logs = new ConcurrentQueue<string>();
        var source = new RecordingRuntimeTestSource();
        var running = new RecordingWorkerRuntime(options, settings, logs.Enqueue, () => source,
            _ => new FlvMediaRecorder(sourceClient, Options.Create(new MediaToolOptions()), new RecordingRuntimeTestSettings())).RunAsync(lifetime.Token);
        try
        {
            var target = new Streamer { RoomId = 1901002, Name = "Worker HTTP 测试", RecordDanmu = true };
            await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(target, cancellationToken: timeout.Token);
            RecordingSegment? segment = null;
            BroadcastSession? session = null;
            await WaitUntilAsync(async () =>
            {
                session = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.StreamerId == target.Id).FirstOrDefaultAsync(timeout.Token);
                if (session is null)
                {
                    return false;
                }
                segment = await fixture.Context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == session.Id && value.DurationMs > 1000).FirstOrDefaultAsync(timeout.Token);
                return segment is { IsClosed: false };
            }, running, logs, timeout.Token);
            var committedBeforeRestart = segment!.CommittedBytes;
            var totalBeforeRestart = (await fixture.Context.Collection<BroadcastSession>("sessions")
                .Find(value => value.Id == session!.Id).SingleAsync(timeout.Token)).TotalBytes;
            var ownerBeforeRestart = await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                .Find(value => value.RoomId == target.RoomId).SingleAsync(timeout.Token);
            Assert.Equal("Worker", ownerBeforeRestart.OwnerKind);
            Assert.Equal(workerId, ownerBeforeRestart.OwnerId);
            var readBeforeRestart = handler.ReadBytes;
            await fixture.App.StopAsync(timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
            Assert.False(running.IsCompleted, string.Join('\n', logs));
            Assert.True(handler.ReadBytes > readBeforeRestart, "Capture stopped while the server was unavailable.\n" + string.Join('\n', logs));
            await fixture.RestartAsync();
            Assert.Equal(options.ApiBaseUrl, fixture.Address.AbsoluteUri);
            await WaitUntilAsync(async () =>
            {
                var resumed = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == session!.Id).SingleAsync(timeout.Token);
                return resumed.TotalBytes > totalBeforeRestart;
            }, running, logs, timeout.Token);
            var retainedSegment = await fixture.Context.Collection<RecordingSegment>("segments")
                .Find(value => value.Id == segment.Id).SingleAsync(timeout.Token);
            Assert.True(retainedSegment.CommittedBytes >= committedBeforeRestart);
            var ownerAfterRestart = await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                .Find(value => value.RoomId == target.RoomId).SingleAsync(timeout.Token);
            Assert.Equal(ownerBeforeRestart.AssignmentId, ownerAfterRestart.AssignmentId);
            Assert.Equal(ownerBeforeRestart.Generation, ownerAfterRestart.Generation);
            Assert.Equal(workerId, ownerAfterRestart.OwnerId);
            var sessions = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.StreamerId == target.Id).ToListAsync(timeout.Token);
            Assert.Equal(session!.Id, Assert.Single(sessions).Id);
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

    private static async Task RunAsync(bool tls, bool restartWorker = false, int roomCount = 1, bool localOverride = false)
    {
        using var certificate = new TranscodingTestCertificate();
        var arguments = new[]
        {
            "--TranscodingControl:Enabled", "true", "--TranscodingControl:ListenAddress", "127.0.0.1",
            "--TranscodingControl:Port", certificate.ControlPort.ToString(),
            "--TranscodingControl:UseTls", tls ? "true" : "false", "--TranscodingControl:CertificatePath", certificate.Path
        };
        await using var fixture = tls
            ? await HttpServerFixture.CreateHttpsAsync(certificate.Path, certificate.Sha256, arguments)
            : await HttpServerFixture.CreateAsync("Production", arguments);
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var globalSettings = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        globalSettings["minimumFreeBytes"] = 0;
        using (var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", globalSettings))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var sampleSession = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 8);
        var sampleSegment = await fixture.Context.Collection<RecordingSegment>("segments").Find(segment => segment.SessionId == sampleSession.Id).SingleAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, sampleSession.DirectoryPath, sampleSegment.RelativePath));
        using var streamHandler = new RecordingLiveTestHttpHandler(bytes, TimeSpan.FromMilliseconds(5));
        using var sourceClient = new HttpClient(streamHandler, disposeHandler: false);
        using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name = "Actual recording runtime", kind = "Recording" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var registration = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = registration["worker"]!["id"]!.GetValue<string>();
        var configuration = new WorkerConfiguration
        {
            TempDirectory = Path.Combine(fixture.DirectoryPath, "recording-node"),
            MinimumFreeBytes = 0,
            MaxCacheBytes = 512L * 1024 * 1024,
            MaxConcurrentEncodes = roomCount
        };
        using (var configured = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + id,
            new { name = "Actual recording runtime", enabled = true, configuration, expectedRevision = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        var options = new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri,
            ControlHost = "127.0.0.1",
            ControlPort = certificate.ControlPort,
            ControlUseTls = tls,
            WorkerId = id,
            Secret = registration["secret"]!.GetValue<string>(),
            ServerCertificateSha256 = tls ? certificate.Sha256 : null
        };
        using var settings = new WorkerSettingsStore(Path.Combine(fixture.DirectoryPath, "recording-node-settings.json"), "Recording");
        await settings.SaveAsync(new() { Worker = options, Configuration = configuration }, CancellationToken.None);
        if (localOverride)
        {
            await settings.SaveRemoteConfigurationAsync(configuration, 2, CancellationToken.None);
            await settings.SaveAsync(settings.Current with
            {
                Configuration = configuration with { UploadBytesPerSecond = 1024 * 1024 }
            }, CancellationToken.None);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var logs = new ConcurrentQueue<string>();
        var captureSource = new RecordingRuntimeTestSource();
        Task StartWorker() => new RecordingWorkerRuntime(options, settings, logs.Enqueue, () => captureSource,
            _ => new FlvMediaRecorder(sourceClient, Options.Create(new MediaToolOptions()), new RecordingRuntimeTestSettings())).RunAsync(lifetime.Token);
        var running = StartWorker();
        try
        {
            var targets = Enumerable.Range(0, roomCount).Select(index => new Streamer
            {
                RoomId = 1901001 + index, Name = "远端录制目标 " + index, RecordDanmu = true
            }).ToArray();
            var target = targets[0];
            await fixture.Context.Collection<Streamer>("streamers").InsertManyAsync(targets, cancellationToken: timeout.Token);
            BroadcastSession? session = null;
            RecordingSegment? segment = null;
            await WaitUntilAsync(async () =>
            {
                session = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.StreamerId == target.Id).FirstOrDefaultAsync(timeout.Token);
                if (session is null)
                {
                    return false;
                }
                segment = await fixture.Context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == session.Id && value.DurationMs > 2000).FirstOrDefaultAsync(timeout.Token);
                return segment is { IsClosed: false };
            }, running, logs, timeout.Token);
            Assert.Null(session!.EndedAtUtc);
            Assert.False(segment!.IsClosed);
            if (localOverride)
            {
                var saved = await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers")
                    .Find(value => value.Id == id).SingleAsync(timeout.Token);
                Assert.Equal(1024 * 1024, saved.Configuration.UploadBytesPerSecond);
                Assert.Equal(3, saved.ConfigurationRevision);
            }
            Assert.InRange(segment.CommittedBytes, 13, new FileInfo(Path.Combine(fixture.StoragePath, session.DirectoryPath, segment.RelativePath)).Length);
            using var playbackResponse = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/sessions/{session.Id}/playback", new { offsetMs = segment.StartOffsetMs + 500 });
            Assert.Equal(HttpStatusCode.OK, playbackResponse.StatusCode);
            var playback = (await playbackResponse.Content.ReadFromJsonAsync<PlaybackResponse>())!;
            await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, playback);
            var eventsPath = Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕", "事件.jsonl");
            await WaitUntilAsync(async () => File.Exists(eventsPath) && (await File.ReadAllTextAsync(eventsPath, timeout.Token)).Contains("录像 Worker 弹幕", StringComparison.Ordinal), running, logs, timeout.Token);
            if (roomCount > 1)
            {
                foreach (var parallelTarget in targets.Skip(1))
                {
                    await WaitUntilAsync(async () =>
                    {
                        var parallelSession = await fixture.Context.Collection<BroadcastSession>("sessions")
                            .Find(value => value.StreamerId == parallelTarget.Id && value.EndedAtUtc == null).FirstOrDefaultAsync(timeout.Token);
                        return parallelSession is not null && parallelSession.Id != session.Id
                            && await fixture.Context.Collection<RecordingSegment>("segments")
                                .Find(value => value.SessionId == parallelSession.Id && value.DurationMs > 2000 && !value.IsClosed)
                                .AnyAsync(timeout.Token);
                    }, running, logs, timeout.Token);
                }
                await WaitUntilAsync(async () => await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers")
                    .Find(value => value.Id == id && value.Telemetry.ActiveRecordings == roomCount).AnyAsync(timeout.Token), running, logs, timeout.Token);
            }
            if (restartWorker)
            {
                var previousSegmentId = segment.Id;
                await lifetime.CancelAsync();
                try
                {
                    await running.WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                lifetime.Dispose();
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                running = StartWorker();
                await WaitUntilAsync(async () => await fixture.Context.Collection<RecordingSegment>("segments")
                    .Find(value => value.SessionId == session.Id && value.Id != previousSegmentId && value.DurationMs > 1000)
                    .AnyAsync(timeout.Token), running, logs, timeout.Token);
                Assert.Single(await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.StreamerId == target.Id).ToListAsync(timeout.Token));
            }
            var targetIds = targets.Select(value => value.Id).ToArray();
            await fixture.Context.Collection<Streamer>("streamers").UpdateManyAsync(value => targetIds.Contains(value.Id),
                Builders<Streamer>.Update.Set(value => value.Enabled, false), cancellationToken: timeout.Token);
            await WaitUntilAsync(async () =>
            {
                var latest = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == session.Id).SingleAsync(timeout.Token);
                var pausedCount = await fixture.Context.Collection<BroadcastSession>("sessions")
                    .CountDocumentsAsync(value => targetIds.Contains(value.StreamerId) && value.EndedAtUtc == null && value.State == "Waiting",
                        cancellationToken: timeout.Token);
                var roomIds = targets.Select(value => value.RoomId).ToArray();
                var releasedCount = await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                    .CountDocumentsAsync(value => roomIds.Contains(value.RoomId) && value.State == "Released", cancellationToken: timeout.Token);
                return latest.EndedAtUtc is null && pausedCount == roomCount && releasedCount == roomCount
                    && !Directory.EnumerateFiles(configuration.TempDirectory, "*.flv", SearchOption.AllDirectories).Any();
            }, running, logs, timeout.Token);
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
            finally
            {
                lifetime.Dispose();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, Task runtime, ConcurrentQueue<string> logs, CancellationToken cancellationToken)
    {
        while (!await predicate())
        {
            if (runtime.IsCompleted)
            {
                await runtime;
                Assert.Fail("Recording runtime ended before the expected state.\n" + string.Join('\n', logs));
            }
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Recording runtime did not reach the expected state.\n" + string.Join('\n', logs));
            }
        }
    }
}
