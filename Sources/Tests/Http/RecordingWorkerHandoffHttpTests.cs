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

/// <summary>验证两个真实录像节点交接后保持逻辑场次及可播放的独立物理归档。</summary>
public sealed class RecordingWorkerHandoffHttpTests
{
    /// <summary>每个节点封口后上传，交接仍保留同一逻辑场次及可播放的独立媒体和弹幕归档。</summary>
    [MongoHttpFact]
    public async Task DifferentWorkersContinueOneLogicalSessionAndPlayTheSecondCapture()
    {
        using var control = new TranscodingTestCertificate();
        await using var fixture = await HttpServerFixture.CreateAsync("Production",
            "--TranscodingControl:Enabled", "true", "--TranscodingControl:ListenAddress", "127.0.0.1",
            "--TranscodingControl:Port", control.ControlPort.ToString(), "--TranscodingControl:UseTls", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var globalSettings = (await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        globalSettings["minimumFreeBytes"] = 0;
        globalSettings["monitorIntervalSeconds"] = 5;
        using (var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", globalSettings))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var sample = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 20);
        var sampleSegment = await fixture.Context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == sample.Id).SingleAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, sample.DirectoryPath, sampleSegment.RelativePath));
        using var handler = new RecordingLiveTestHttpHandler(bytes, TimeSpan.FromMilliseconds(5));
        using var sourceClient = new HttpClient(handler, disposeHandler: false);
        var secondSample = await PlaybackHttpMediaFixture.SeedRecordingAsync(fixture, 40);
        var secondSampleSegment = await fixture.Context.Collection<RecordingSegment>("segments")
            .Find(value => value.SessionId == secondSample.Id).SingleAsync();
        var secondBytes = await File.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, secondSample.DirectoryPath, secondSampleSegment.RelativePath));
        using var secondHandler = new RecordingLiveTestHttpHandler(secondBytes, TimeSpan.FromMilliseconds(5));
        using var secondSourceClient = new HttpClient(secondHandler, disposeHandler: false);
        var first = await RegisterAsync(fixture, control.ControlPort, "first");
        using var firstSettings = first.Settings;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var secondLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var logs = new ConcurrentQueue<string>();
        var firstSource = new RecordingRuntimeTestSource();
        var runningFirst = new RecordingWorkerRuntime(first.Options, firstSettings, value => logs.Enqueue("A: " + value), () => firstSource,
            _ => new FlvMediaRecorder(sourceClient, Options.Create(new MediaToolOptions()), new RecordingRuntimeTestSettings())).RunAsync(firstLifetime.Token);
        Task? runningSecond = null;
        WorkerSettingsStore? secondSettings = null;
        try
        {
            var target = new Streamer { RoomId = 1901101, Name = "跨节点接续主播", RecordDanmu = true };
            await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(target, cancellationToken: timeout.Token);
            RecordingSessionTaskSnapshot? firstCapture = null;
            await WaitUntilAsync(async () =>
            {
                firstCapture = await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks")
                    .Find(value => value.TargetId == target.Id && value.WorkerId == first.Id
                        && value.CaptureState == "Recording" && value.CapturedDurationMs > 2000)
                    .FirstOrDefaultAsync(timeout.Token);
                return firstCapture is not null;
            }, runningFirst, logs, timeout.Token);
            var logicalId = firstCapture!.Descriptor.LogicalSessionId ?? firstCapture.Id;
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            await AssertCaptureQueuedAsync(fixture, firstCapture, firstSettings, timeout.Token);
            var node = await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers").Find(value => value.Id == first.Id).SingleAsync(timeout.Token);
            using (var disabled = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + first.Id,
                new { name = node.Name, enabled = false, configuration = node.Configuration, expectedRevision = node.ConfigurationRevision }))
            {
                Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            }
            await WaitUntilAsync(async () =>
            {
                var owner = await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                    .Find(value => value.RoomId == target.RoomId).SingleAsync(timeout.Token);
                var session = await fixture.Context.Collection<BroadcastSession>("sessions")
                    .Find(value => value.Id == logicalId).FirstOrDefaultAsync(timeout.Token);
                return owner.State == "Released" && session is { EndedAtUtc: null, State: "Waiting" }
                    && await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
                        .Find(value => value.Id == firstCapture.Id && value.IsClosed).AnyAsync(timeout.Token)
                    && await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions")
                        .Find(value => value.Id == firstCapture.Id && value.FinalizedAtUtc != null).AnyAsync(timeout.Token);
            }, runningFirst, logs, timeout.Token);
            var original = await fixture.Context.Collection<BroadcastSession>("sessions")
                .Find(value => value.Id == logicalId).SingleAsync(timeout.Token);
            var firstBinding = await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
                .Find(value => value.Id == firstCapture.Id).SingleAsync(timeout.Token);
            Assert.False(Directory.Exists(CaptureDirectory(firstSettings, firstCapture.Id)));
            Assert.True((await fixture.Context.Collection<Streamer>("streamers").Find(value => value.Id == target.Id).SingleAsync(timeout.Token)).Enabled);
            var firstSegments = await fixture.Context.Collection<RecordingSegment>("segments")
                .Find(value => value.SessionId == logicalId && value.AttemptId == firstBinding.AssignmentId).ToListAsync(timeout.Token);
            Assert.NotEmpty(firstSegments);
            Assert.All(firstSegments, value => Assert.True(value.IsClosed));
            var firstEnd = firstSegments.Max(value => value.StartOffsetMs + value.DurationMs);

            var second = await RegisterAsync(fixture, control.ControlPort, "second");
            secondSettings = second.Settings;
            var secondSource = new RecordingRuntimeTestSource();
            runningSecond = new RecordingWorkerRuntime(second.Options, secondSettings, value => logs.Enqueue("B: " + value), () => secondSource,
                _ => new FlvMediaRecorder(secondSourceClient, Options.Create(new MediaToolOptions()), new RecordingRuntimeTestSettings())).RunAsync(secondLifetime.Token);
            RecordingSessionTaskSnapshot? secondCapture = null;
            await WaitUntilAsync(async () =>
            {
                secondCapture = await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks")
                    .Find(value => value.TargetId == target.Id && value.WorkerId == second.Id
                        && value.CaptureState == "Recording" && value.CapturedDurationMs > firstEnd + 2000)
                    .FirstOrDefaultAsync(timeout.Token);
                return secondCapture is not null;
            }, runningSecond, logs, timeout.Token);
            Assert.Equal(logicalId, secondCapture!.Descriptor.LogicalSessionId);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            await AssertCaptureQueuedAsync(fixture, secondCapture, secondSettings, timeout.Token);
            Assert.Single(await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
                .Find(value => value.LogicalSessionId == logicalId).ToListAsync(timeout.Token));
            var secondNode = await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers")
                .Find(value => value.Id == second.Id).SingleAsync(timeout.Token);
            using (var disabled = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + second.Id,
                new { name = secondNode.Name, enabled = false, configuration = secondNode.Configuration,
                    expectedRevision = secondNode.ConfigurationRevision }))
            {
                Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            }
            await WaitUntilAsync(async () =>
                await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions")
                    .Find(value => value.Id == secondCapture.Id && value.Descriptor.IsClosed && value.FinalizedAtUtc != null)
                    .AnyAsync(timeout.Token)
                && await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships")
                    .Find(value => value.RoomId == target.RoomId && value.State == "Released").AnyAsync(timeout.Token),
                runningSecond, logs, timeout.Token);
            var secondBinding = await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
                .Find(value => value.Id == secondCapture.Id && value.IsClosed).SingleAsync(timeout.Token);
            var secondSegment = await fixture.Context.Collection<RecordingSegment>("segments")
                .Find(value => value.SessionId == logicalId && value.AttemptId == secondBinding.AssignmentId && value.DurationMs > 2000)
                .SortBy(value => value.StartOffsetMs).FirstAsync(timeout.Token);
            var playbackOffset = Math.Max(secondSegment.StartOffsetMs + 500, firstEnd + 500);
            Assert.True(secondSegment.StartOffsetMs + secondSegment.DurationMs > playbackOffset + 500);
            Assert.True(secondSegment.IsClosed);
            Assert.False(Directory.Exists(CaptureDirectory(secondSettings, secondCapture.Id)));
            Assert.NotEqual(firstBinding.Id, secondBinding.Id);
            Assert.NotEqual(firstBinding.AssignmentId, secondBinding.AssignmentId);
            Assert.Equal(Path.Combine(original.DirectoryPath, ".captures", secondBinding.Id), secondBinding.DirectoryPath);
            Assert.StartsWith(secondBinding.DirectoryPath + Path.DirectorySeparatorChar, secondSegment.RootRelativePath);
            Assert.True(File.Exists(Path.Combine(fixture.StoragePath, secondSegment.RootRelativePath!)));
            var sessions = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.StreamerId == target.Id).ToListAsync(timeout.Token);
            var continued = Assert.Single(sessions);
            Assert.Equal(logicalId, continued.Id);
            Assert.Null(continued.EndedAtUtc);
            Assert.Equal(2, continued.EventSources.Count);
            Assert.Equal(2, continued.EventSources.Select(value => value.DirectoryPath).Distinct(StringComparer.Ordinal).Count());
            foreach (var eventSource in continued.EventSources)
            {
                var eventsPath = Path.Combine(fixture.StoragePath, eventSource.DirectoryPath, "弹幕", "事件.jsonl");
                await WaitUntilAsync(async () => File.Exists(eventsPath)
                    && (await File.ReadAllTextAsync(eventsPath, timeout.Token)).Contains("录像 Worker 弹幕", StringComparison.Ordinal),
                    runningSecond, logs, timeout.Token);
            }
            using var playbackResponse = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/sessions/{logicalId}/playback", new { offsetMs = playbackOffset });
            Assert.Equal(HttpStatusCode.OK, playbackResponse.StatusCode);
            var playback = (await playbackResponse.Content.ReadFromJsonAsync<PlaybackResponse>())!;
            await PlaybackHttpMediaFixture.DownloadAndDecodeAsync(fixture, playback);
        }
        finally
        {
            await firstLifetime.CancelAsync();
            await secondLifetime.CancelAsync();
            try
            {
                await Task.WhenAll(StopAsync(runningFirst, firstLifetime), StopAsync(runningSecond, secondLifetime));
            }
            finally
            {
                secondSettings?.Dispose();
            }
        }
    }

    private static async Task AssertCaptureQueuedAsync(HttpServerFixture fixture, RecordingSessionTaskSnapshot capture,
        WorkerSettingsStore settings, CancellationToken cancellationToken)
    {
        var snapshot = await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks")
            .Find(value => value.Id == capture.Id).SingleAsync(cancellationToken);
        Assert.False(snapshot.Descriptor.IsClosed);
        Assert.Equal("Recording", snapshot.CaptureState);
        Assert.Equal("Queued", snapshot.UploadState);
        Assert.Equal(0, snapshot.AcknowledgedArchiveBytes);
        Assert.Null(snapshot.CurrentTransfer);
        Assert.False(await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions")
            .Find(value => value.Id == capture.Id).AnyAsync(cancellationToken));
        Assert.False(await fixture.Context.Collection<RecordingSegmentReceipt>("recordingSegments")
            .Find(value => value.SessionId == capture.Id).AnyAsync(cancellationToken));
        var directory = CaptureDirectory(settings, capture.Id);
        Assert.True(File.Exists(Path.Combine(directory, "session.json")));
        Assert.True(Directory.EnumerateFiles(directory, "*.flv", SearchOption.AllDirectories).Any());
        Assert.False(File.Exists(Path.Combine(directory, "uploaded.json")));
    }

    private static string CaptureDirectory(WorkerSettingsStore settings, string sessionId) =>
        Path.Combine(settings.Current.Configuration.TempDirectory, "live-recorder-recording-worker", settings.Current.Worker.WorkerId,
            "sessions", sessionId);

    private static async Task<(string Id, WorkerConnectionOptions Options, WorkerSettingsStore Settings)> RegisterAsync(
        HttpServerFixture fixture, int controlPort, string name)
    {
        using var created = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/transcoding/workers", new { name, kind = "Recording" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var registration = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = registration["worker"]!["id"]!.GetValue<string>();
        var configuration = new WorkerConfiguration
        {
            TempDirectory = Path.Combine(fixture.DirectoryPath, name + "-recording-spool"), MinimumFreeBytes = 0,
            MaxCacheBytes = 512L * 1024 * 1024, MaxConcurrentEncodes = 1
        };
        using (var configured = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/transcoding/workers/" + id,
            new { name, enabled = true, configuration, expectedRevision = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        var options = new WorkerConnectionOptions
        {
            ApiBaseUrl = fixture.Address.AbsoluteUri, ControlHost = "127.0.0.1", ControlPort = controlPort,
            ControlUseTls = false, WorkerId = id, Secret = registration["secret"]!.GetValue<string>()
        };
        var settings = new WorkerSettingsStore(Path.Combine(fixture.DirectoryPath, name + "-recording-settings.json"), "Recording");
        try
        {
            await settings.SaveAsync(new() { Worker = options, Configuration = configuration }, CancellationToken.None);
            return (id, options, settings);
        }
        catch
        {
            settings.Dispose();
            throw;
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, Task runtime, ConcurrentQueue<string> logs, CancellationToken cancellationToken)
    {
        while (!await predicate())
        {
            if (runtime.IsCompleted)
            {
                await runtime;
                Assert.Fail("录像节点在交接条件达成前退出。\n" + string.Join('\n', logs));
            }
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("录像节点未达到预期交接状态。\n" + string.Join('\n', logs));
            }
        }
    }

    private static async Task StopAsync(Task? runtime, CancellationTokenSource lifetime)
    {
        if (runtime is null)
        {
            return;
        }
        try
        {
            await runtime.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }
}
