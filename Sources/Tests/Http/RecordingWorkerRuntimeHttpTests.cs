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
    /// <summary>The node keeps an open broadcast local, then uploads playable media, index and events after capture ends.</summary>
    [MongoHttpFact]
    public Task RecordingUploadsOnlyAfterCaptureStops() => RunAsync(tls: false);

    /// <summary>The same recording flow supports pinned TLS control and HTTPS transfers.</summary>
    [MongoHttpFact]
    public Task RecordingSupportsPinnedTlsControlAndHttps() => RunAsync(tls: true);

    /// <summary>A restarted Worker recovers its durable assignment and continues the existing session.</summary>
    [MongoHttpFact]
    public Task WorkerRestartResumesTheExistingSession() => RunAsync(tls: false, restartWorker: true);

    /// <summary>One room uploads after stopping while another room keeps recording within the same node.</summary>
    [MongoHttpFact]
    public Task OneWorkerUploadsAClosedRoomWhileAnotherRoomKeepsRecording() => RunAsync(tls: false, roomCount: 2);

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
        var sample = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 40);
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
            var captured = await WaitForCaptureAsync(fixture, target.Id, workerId, running, logs, timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            await AssertCaptureQueuedAsync(fixture, captured, configuration.TempDirectory, workerId, timeout.Token);
            var ownerBeforeRestart = await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                .Find(value => value.RoomId == target.RoomId).SingleAsync(timeout.Token);
            Assert.Equal("Worker", ownerBeforeRestart.OwnerKind);
            Assert.Equal(workerId, ownerBeforeRestart.OwnerId);
            var readBeforeRestart = handler.ReadBytes;
            await fixture.App.StopAsync(timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
            Assert.False(running.IsCompleted, string.Join('\n', logs));
            Assert.True(handler.ReadBytes > readBeforeRestart, "Capture stopped while the server was unavailable.\n" + string.Join('\n', logs));
            var serverRestartedAtUtc = DateTime.UtcNow;
            await fixture.RestartAsync();
            Assert.Equal(options.ApiBaseUrl, fixture.Address.AbsoluteUri);
            var resumed = await WaitForCaptureAsync(fixture, target.Id, workerId, running, logs, timeout.Token,
                captured.CapturedDurationMs + 1000, serverRestartedAtUtc);
            Assert.Equal(captured.Id, resumed.Id);
            await AssertCaptureQueuedAsync(fixture, resumed, configuration.TempDirectory, workerId, timeout.Token);
            var ownerAfterRestart = await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                .Find(value => value.RoomId == target.RoomId).SingleAsync(timeout.Token);
            Assert.Equal(ownerBeforeRestart.AssignmentId, ownerAfterRestart.AssignmentId);
            Assert.Equal(ownerBeforeRestart.Generation, ownerAfterRestart.Generation);
            Assert.Equal(workerId, ownerAfterRestart.OwnerId);

            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            await StopTargetAsync(fixture, target);
            var archived = await WaitForArchiveAsync(fixture, resumed, configuration.TempDirectory, workerId,
                running, logs, timeout.Token);
            Assert.Equal(captured.Id, archived.Id);
            Assert.Single(await fixture.Context.Collection<BroadcastSession>("sessions")
                .Find(value => value.StreamerId == target.Id).ToListAsync(timeout.Token));
            Assert.True(archived.TotalBytes > 0);
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
        var sampleSession = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 40);
        var sampleSegment = await fixture.Context.Collection<RecordingSegment>("segments").Find(segment => segment.SessionId == sampleSession.Id).SingleAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, sampleSession.DirectoryPath, sampleSegment.RelativePath));
        using var streamHandler = new RecordingLiveTestHttpHandler(bytes, TimeSpan.FromMilliseconds(50));
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
            var captures = new List<RecordingSessionTaskSnapshot>();
            foreach (var selectedTarget in targets)
            {
                captures.Add(await WaitForCaptureAsync(fixture, selectedTarget.Id, id, running, logs, timeout.Token));
            }
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            foreach (var capture in captures)
            {
                await AssertCaptureQueuedAsync(fixture, capture, configuration.TempDirectory, id, timeout.Token);
            }
            var captured = captures[0];
            if (localOverride)
            {
                var saved = await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers")
                    .Find(value => value.Id == id).SingleAsync(timeout.Token);
                Assert.Equal(1024 * 1024, saved.Configuration.UploadBytesPerSecond);
                Assert.Equal(3, saved.ConfigurationRevision);
            }
            await WaitUntilAsync(async () => await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers")
                .Find(value => value.Id == id && value.Telemetry.ActiveRecordings == roomCount).AnyAsync(timeout.Token),
                running, logs, timeout.Token);
            if (restartWorker)
            {
                var directory = Path.Combine(configuration.TempDirectory, "live-recorder-recording-worker", id,
                    "sessions", captured.Id, "录制提交");
                var previousSegments = Directory.EnumerateDirectories(directory).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
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
                var workerRestartedAtUtc = DateTime.UtcNow;
                running = StartWorker();
                var resumed = await WaitForCaptureAsync(fixture, target.Id, id, running, logs, timeout.Token,
                    captured.CapturedDurationMs + 1000, workerRestartedAtUtc);
                Assert.Equal(captured.Id, resumed.Id);
                await WaitUntilAsync(() => Task.FromResult(Directory.EnumerateDirectories(directory)
                    .Any(value => !previousSegments.Contains(Path.GetFileName(value)))), running, logs, timeout.Token);
                Assert.Contains(Directory.EnumerateDirectories(directory), value => !previousSegments.Contains(Path.GetFileName(value)));
                await AssertCaptureQueuedAsync(fixture, resumed, configuration.TempDirectory, id, timeout.Token);
            }

            await StopTargetAsync(fixture, target);
            var archived = await WaitForArchiveAsync(fixture, captured, configuration.TempDirectory, id,
                running, logs, timeout.Token);
            Assert.Null(archived.EndedAtUtc);
            Assert.Equal("Waiting", archived.State);
            var segments = await fixture.Context.Collection<RecordingSegment>("segments")
                .Find(value => value.SessionId == archived.Id).SortBy(value => value.StartOffsetMs).ToListAsync(timeout.Token);
            Assert.NotEmpty(segments);
            Assert.All(segments, value => Assert.True(value.IsClosed));
            if (restartWorker)
            {
                Assert.True(segments.Count >= 2);
            }
            if (roomCount > 1)
            {
                var readingBefore = streamHandler.ReadBytes;
                foreach (var capture in captures.Skip(1))
                {
                    var stillRecording = await WaitForCaptureAsync(fixture, capture.TargetId, id, running, logs, timeout.Token);
                    Assert.Equal(capture.Id, stillRecording.Id);
                    await AssertCaptureQueuedAsync(fixture, stillRecording, configuration.TempDirectory, id, timeout.Token);
                }
                await WaitUntilAsync(() => Task.FromResult(streamHandler.ReadBytes > readingBefore), running, logs, timeout.Token);
                await WaitUntilAsync(async () => await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers")
                    .Find(value => value.Id == id && value.Telemetry.ActiveRecordings == roomCount - 1).AnyAsync(timeout.Token),
                    running, logs, timeout.Token);
            }
            using var playbackResponse = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/sessions/{archived.Id}/playback",
                new { offsetMs = segments[0].StartOffsetMs + 500 });
            Assert.Equal(HttpStatusCode.OK, playbackResponse.StatusCode);
            var playback = (await playbackResponse.Content.ReadFromJsonAsync<PlaybackResponse>())!;
            await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, playback);
            var eventsPath = Path.Combine(fixture.StoragePath, archived.DirectoryPath, "弹幕", "事件.jsonl");
            Assert.Contains("录像 Worker 弹幕", await File.ReadAllTextAsync(eventsPath, timeout.Token), StringComparison.Ordinal);
            foreach (var selectedTarget in targets.Skip(1))
            {
                await StopTargetAsync(fixture, selectedTarget);
                var capture = captures.Single(value => value.TargetId == selectedTarget.Id);
                await WaitForArchiveAsync(fixture, capture, configuration.TempDirectory, id, running, logs, timeout.Token);
            }
            Assert.False(Directory.EnumerateFiles(configuration.TempDirectory, "*.flv", SearchOption.AllDirectories).Any());
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

    private static async Task<RecordingSessionTaskSnapshot> WaitForCaptureAsync(HttpServerFixture fixture, string targetId,
        string workerId, Task runtime, ConcurrentQueue<string> logs, CancellationToken cancellationToken,
        double minimumDurationMs = 2000, DateTime reportedAfterUtc = default)
    {
        RecordingSessionTaskSnapshot? snapshot = null;
        await WaitUntilAsync(async () =>
        {
            snapshot = await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks")
                .Find(value => value.TargetId == targetId && value.WorkerId == workerId
                    && value.CaptureState == "Recording" && value.CapturedDurationMs > minimumDurationMs
                    && !value.Descriptor.IsClosed && value.LastReportedAtUtc >= reportedAfterUtc)
                .SortByDescending(value => value.Descriptor.StartedAtUtc).FirstOrDefaultAsync(cancellationToken);
            return snapshot is not null;
        }, runtime, logs, cancellationToken);
        return snapshot!;
    }

    private static async Task AssertCaptureQueuedAsync(HttpServerFixture fixture, RecordingSessionTaskSnapshot capture,
        string temporaryDirectory, string workerId, CancellationToken cancellationToken)
    {
        var current = await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks")
            .Find(value => value.Id == capture.Id).SingleAsync(cancellationToken);
        Assert.False(current.Descriptor.IsClosed);
        Assert.Equal("Recording", current.CaptureState);
        Assert.Equal("Queued", current.UploadState);
        Assert.True(current.CommittedArchiveBytes > 0);
        Assert.Equal(0, current.AcknowledgedArchiveBytes);
        Assert.Null(current.CurrentTransfer);
        Assert.False(await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions")
            .Find(value => value.Id == capture.Id).AnyAsync(cancellationToken));
        Assert.False(await fixture.Context.Collection<RecordingSegmentReceipt>("recordingSegments")
            .Find(value => value.SessionId == capture.Id).AnyAsync(cancellationToken));
        var directory = Path.Combine(temporaryDirectory, "live-recorder-recording-worker", workerId, "sessions", capture.Id);
        Assert.True(File.Exists(Path.Combine(directory, "session.json")));
        Assert.True(Directory.EnumerateFiles(directory, "*.flv", SearchOption.AllDirectories).Any());
        Assert.False(File.Exists(Path.Combine(directory, "uploaded.json")));
    }

    private static async Task StopTargetAsync(HttpServerFixture fixture, Streamer target)
    {
        target.Enabled = false;
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/streamers/" + target.Id, target);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<BroadcastSession> WaitForArchiveAsync(HttpServerFixture fixture, RecordingSessionTaskSnapshot capture,
        string temporaryDirectory, string workerId, Task runtime, ConcurrentQueue<string> logs, CancellationToken cancellationToken)
    {
        await WaitUntilAsync(async () =>
        {
            var finalized = await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions")
                .Find(value => value.Id == capture.Id && value.Descriptor.IsClosed && value.FinalizedAtUtc != null)
                .AnyAsync(cancellationToken);
            var released = await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                .Find(value => value.RoomId == capture.Descriptor.RoomId && value.State == "Released").AnyAsync(cancellationToken);
            return finalized && released;
        }, runtime, logs, cancellationToken);
        var directory = Path.Combine(temporaryDirectory, "live-recorder-recording-worker", workerId, "sessions", capture.Id);
        Assert.False(Directory.Exists(directory));
        var logicalId = capture.Descriptor.LogicalSessionId ?? capture.Id;
        return await fixture.Context.Collection<BroadcastSession>("sessions")
            .Find(value => value.Id == logicalId).SingleAsync(cancellationToken);
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
