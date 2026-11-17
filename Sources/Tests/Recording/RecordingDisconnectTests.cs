using System.Diagnostics;
using System.Text.Json;
using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证真实 FLV 在网络中断、无数据连接和恢复时的场次生命周期。</summary>
public sealed class RecordingDisconnectTests
{
    /// <summary>媒体和直播查询同时失败后按时结束归档，保留目标监控并停止弹幕。</summary>
    [RecordingFact]
    public async Task CompleteNetworkFailureCompletesSessionAtItsLastMediaTime()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (target, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.QueryFailure = new HttpRequestException("Network unavailable");
        room.StreamFailure = new HttpRequestException("Network unavailable");
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && (await fixture.SegmentsAsync()).All(segment => segment.IsClosed), "failed network closes the current media connection");
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(Assert.Single(await fixture.SessionsAsync()).EndedAtUtc);

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForCompletedAsync(fixture, room);

        var session = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(session.LastMediaAtUtc, session.EndedAtUtc);
        var saved = await fixture.Context.Collection<Streamer>("streamers").Find(item => item.Id == target.Id).SingleAsync();
        Assert.True(saved.Enabled);
        Assert.True(saved.IsMonitored);
        var manifest = await ReadManifestAsync(fixture, session);
        Assert.Equal("Completed", manifest.Session.State);
        Assert.Equal(session.EndedAtUtc, manifest.Session.EndedAtUtc);
        Assert.All(manifest.Segments, segment => Assert.True(segment.IsClosed));
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>时限内恢复媒体复用原连接与场次，并为下一次断流重新计时。</summary>
    [RecordingFact]
    public async Task MediaRecoveryBeforeDeadlineKeepsSessionAndRestartsItsDeadline()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        var original = Assert.Single(await fixture.SessionsAsync());
        room.PauseMedia();
        await room.MediaPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(Assert.Single(await fixture.SessionsAsync()).EndedAtUtc);
        var bytes = original.TotalBytes;

        room.ResumeMedia();
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(session => session.TotalBytes > bytes), "resumed media reaches the existing session");
        room.PauseMedia();
        await room.MediaPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(59));
        var continued = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(original.Id, continued.Id);
        Assert.Null(continued.EndedAtUtc);
        Assert.Equal(1, room.Connections);

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForCompletedAsync(fixture, room);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>媒体读无数据且直播查询不返回时，独立截止时间仍能取消全部采集任务。</summary>
    [RecordingFact]
    public async Task StalledMediaAndBlockedRoomLookupCannotPreventTimeout()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.PauseMedia();
        await room.MediaPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        room.BlockNextQuery();
        clock.Advance(TimeSpan.FromSeconds(1));
        await room.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(59));
        await WaitForCompletedAsync(fixture, room);

        Assert.False(room.ReleaseQuery.Task.IsCompleted);
        Assert.Equal(1, room.Connections);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>超时后的无媒体重试不发布空场次，真正恢复时创建新场且保留首个关键帧。</summary>
    [RecordingFact]
    public async Task RecoveryAfterTimeoutCreatesOneNewSessionWithoutPublishingEmptyRetries()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        var original = Assert.Single(await fixture.SessionsAsync());
        room.QueryFailure = new HttpRequestException("Network unavailable");
        room.StreamFailure = new HttpRequestException("Network unavailable");
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && (await fixture.SegmentsAsync()).All(segment => segment.IsClosed), "failed network releases the media connection");
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForCompletedAsync(fixture, room);
        var originalEnd = Assert.Single(await fixture.SessionsAsync()).EndedAtUtc;
        var requests = room.StreamRequests;
        room.QueryFailure = null;
        room.StreamFailure = null;
        room.HeaderOnly = true;

        await AdvanceUntilAsync(clock, () => Task.FromResult(room.StreamRequests >= requests + 3), "monitor retries media after timeout");

        Assert.Single(await fixture.SessionsAsync());
        Assert.Single(Directory.EnumerateFiles(fixture.StoragePath, "场次.json", SearchOption.AllDirectories));
        Assert.Equal(0, room.ActiveSubscriptions);
        room.HeaderOnly = false;
        await AdvanceUntilAsync(clock, async () => (await fixture.SessionsAsync()).Any(session => session.Id != original.Id && session.TotalBytes > 0), "effective media starts a new session");
        await fixture.StopSupervisorAsync();
        var sessions = await fixture.SessionsAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(originalEnd, sessions.Single(session => session.Id == original.Id).EndedAtUtc);
        var recovered = sessions.Single(session => session.Id != original.Id);
        Assert.Equal(original.LiveTime, recovered.LiveTime);
        Assert.True(recovered.TotalBytes > 0);
        var segments = await fixture.SegmentsAsync();
        var originalSegment = segments.Where(segment => segment.SessionId == original.Id).MinBy(segment => segment.StartOffsetMs)!;
        var recoveredSegment = segments.Where(segment => segment.SessionId == recovered.Id).MinBy(segment => segment.StartOffsetMs)!;
        Assert.Equal(await FirstSourceKeyframeAsync(fixture, original, originalSegment), await FirstSourceKeyframeAsync(fixture, recovered, recoveredSegment));
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>持续有效媒体在接口查询失败时仍定期更新活动时间，不因累计运行时间被误停。</summary>
    [RecordingFact]
    public async Task ActiveMediaSurvivesRoomLookupFailuresBeyondTheConfiguredTimeout()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.QueryFailure = new HttpRequestException("Room status unavailable");
        for (var index = 0; index < 4; index++)
        {
            var bytes = Assert.Single(await fixture.SessionsAsync()).TotalBytes;
            clock.Advance(TimeSpan.FromSeconds(20));
            await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(session => session.TotalBytes > bytes), "active media continues despite room lookup failures");
        }

        var session = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal("Recording", session.State);
        Assert.Null(session.EndedAtUtc);
        Assert.Equal(1, room.ActiveStreams);
        Assert.Equal(1, room.Connections);
        await fixture.StopSupervisorAsync();
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>只有结束刷新时才上报的短流也会激活归档，不通过二次拉流补救首段。</summary>
    [RecordingFact]
    public async Task TailFlushedShortStreamPublishesItsMediaWithoutAnotherConnection()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await RecordingFixture.CreateAsync(0.2, timeProvider: clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        room.PaceScale = 0;
        await fixture.StartSupervisorAsync(new FinalFlushMediaRecorder(fixture.Recorder));

        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(session => session.TotalBytes > 0) && room.ActiveStreams == 0, "finally-flushed media activates the session");
        await fixture.StopSupervisorAsync();

        var session = Assert.Single(await fixture.SessionsAsync());
        Assert.True(session.TotalBytes > 0);
        Assert.Equal(1, room.Connections);
        Assert.Contains(await fixture.SegmentsAsync(), segment => segment.DurationMs > 0 && segment.IsClosed);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>断流取消触发的最后刷新仍保留已收到的有效媒体，并立即完成该场次。</summary>
    [RecordingFact]
    public async Task TailFlushedMediaIsRetainedWhenInactivityCancelsTheConnection()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync(new FinalFlushMediaRecorder(fixture.Recorder));
        await RecordingFixture.WaitUntilAsync(() => Task.FromResult(room.ReadBytes > fixture.SampleBytes / 5), "real media is buffered before the final progress flush");
        room.PauseMedia();
        await room.MediaPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await fixture.SessionsAsync());

        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForCompletedAsync(fixture, room);

        var session = Assert.Single(await fixture.SessionsAsync());
        Assert.True(session.TotalBytes > 0);
        Assert.Equal(session.LastMediaAtUtc, session.EndedAtUtc);
        Assert.Equal(1, room.Connections);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>首次场次发布缺失数据库记录时，下播与超时收尾会补偿保存已收到的媒体。</summary>
    [RecordingFact]
    public async Task FailedInitialPublicationRecoversMissingSessionWithoutFurtherMedia()
    {
        var clock = new ManualRecordingTimeProvider();
        var captureReady = new TaskCompletionSource<(RecordingFixture Fixture, ControlledRoom Room)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionPublications = 0;
        var publicationFailed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new CallbackRecordingPublisher(async (kind, id, token) =>
        {
            if (kind != "session" || Interlocked.Increment(ref sessionPublications) != 1)
            {
                return;
            }
            var (activeFixture, activeRoom) = await captureReady.Task.WaitAsync(token);
            await activeFixture.Context.Collection<BroadcastSession>("sessions").DeleteOneAsync(session => session.Id == id, token);
            activeRoom.IsLive = false;
            activeRoom.StreamFailure = new HttpRequestException("Media unavailable after the initial publication failure");
            publicationFailed.TrySetResult(id);
            throw new IOException("Initial session publication did not complete");
        });
        await using var fixture = await RecordingFixture.CreateAsync(30, publisher: publisher, timeProvider: clock);
        var settings = await fixture.Settings.GetAsync();
        settings.StreamDisconnectTimeoutMinutes = 1;
        await fixture.Settings.SaveAsync(settings);
        var (_, room) = await fixture.AddRoomAsync(100);
        captureReady.SetResult((fixture, room));
        await fixture.StartSupervisorAsync();
        var sessionId = await publicationFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && (await fixture.SegmentsAsync()).Any(segment => segment.SessionId == sessionId && segment.IsClosed), "failed publication closes and commits its media segment");

        await AdvanceUntilAsync(clock, async () =>
        {
            var candidate = (await fixture.SessionsAsync()).FirstOrDefault(session => session.Id == sessionId && session.State == "Waiting");
            return candidate is not null && (await ReadManifestAsync(fixture, candidate)).Session.State == "Waiting";
        }, "offline state compensates for the missing session document");

        var waiting = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(sessionId, waiting.Id);
        Assert.True(waiting.TotalBytes > 0);
        Assert.Null(waiting.EndedAtUtc);
        var waitingManifest = await ReadManifestAsync(fixture, waiting);
        Assert.Equal("Waiting", waitingManifest.Session.State);
        Assert.NotEmpty(waitingManifest.Segments);
        Assert.All(waitingManifest.Segments, segment => Assert.True(segment.IsClosed));

        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForCompletedAsync(fixture, room);

        var completed = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(sessionId, completed.Id);
        Assert.True(completed.TotalBytes > 0);
        Assert.Equal(completed.LastMediaAtUtc, completed.EndedAtUtc);
        Assert.Equal(1, room.Connections);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>合法的零时长头信息进度不会推进聚合场次和持久化清单的最后媒体时间。</summary>
    [RecordingFact]
    public async Task HeaderOnlyProgressDoesNotAdvanceTheLastEffectiveMediaTime()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        room.DisconnectAfterMs = 1500;
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && (await fixture.SegmentsAsync()).Any(segment => segment.IsClosed && segment.DurationMs > 0), "first effective media connection is committed");
        await fixture.StopSupervisorAsync();
        var original = Assert.Single(await fixture.SessionsAsync());
        var lastMediaAtUtc = original.LastMediaAtUtc;
        var directory = MediaPaths.Resolve(fixture.StoragePath, original.DirectoryPath);
        var relativePath = Path.Combine("录像", "header-only.flv");
        var indexRelativePath = Path.ChangeExtension(relativePath, ".index.jsonl");
        var source = await File.ReadAllBytesAsync(Path.Combine(fixture.DirectoryPath, "source.flv"));
        await File.WriteAllBytesAsync(Path.Combine(directory, relativePath), source[..13]);
        await File.WriteAllTextAsync(Path.Combine(directory, indexRelativePath), "");
        var update = new MediaRecordingUpdate
        {
            Kind = "Committed",
            SegmentId = Guid.NewGuid().ToString("N"),
            RelativePath = relativePath,
            IndexRelativePath = indexRelativePath,
            StartOffsetMs = (original.LastMediaAtUtc - original.StartedAtUtc).TotalMilliseconds + 10000,
            DurationMs = 0,
            CommittedBytes = 13
        };

        await using (var sink = new RecordingProgressSink(original, await fixture.SegmentsAsync(), Guid.NewGuid().ToString("N"), directory, fixture.Context, fixture.Publisher, NullLogger<RecordingProgressSink>.Instance))
        {
            sink.Report(update);
            update.Kind = "Closed";
            sink.Report(update);
        }

        var saved = Assert.Single(await fixture.SessionsAsync());
        var manifest = await ReadManifestAsync(fixture, saved);
        Assert.InRange((saved.LastMediaAtUtc - lastMediaAtUtc).TotalMilliseconds, -1, 1);
        Assert.InRange((manifest.Session.LastMediaAtUtc - lastMediaAtUtc).TotalMilliseconds, -1, 1);
        Assert.Contains(manifest.Segments, segment => segment.Id == update.SegmentId && segment.DurationMs <= 0 && segment.IsClosed);
        await fixture.AssertCommittedFilesAsync(true);
    }

    private static async Task<RecordingFixture> CreateFixtureAsync(ManualRecordingTimeProvider clock)
    {
        var fixture = await RecordingFixture.CreateAsync(30, timeProvider: clock);
        try
        {
            var settings = await fixture.Settings.GetAsync();
            settings.StreamDisconnectTimeoutMinutes = 1;
            await fixture.Settings.SaveAsync(settings);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static Task WaitForMediaAsync(RecordingFixture fixture, ControlledRoom room)
    {
        return RecordingFixture.WaitUntilAsync(async () =>
        {
            if (room.ActiveStreams != 1 || room.ActiveSubscriptions != 1)
            {
                return false;
            }
            var sessions = await fixture.SessionsAsync();
            var segments = await fixture.SegmentsAsync();
            return sessions.Any(session => session is { State: "Recording", EndedAtUtc: null, TotalBytes: > 0 }
                && segments.Any(segment => segment.SessionId == session.Id && segment.DurationMs > 1000));
        }, "committed FLV media and its canonical recording session are published");
    }

    private static Task WaitForCompletedAsync(RecordingFixture fixture, ControlledRoom room)
    {
        return RecordingFixture.WaitUntilAsync(async () =>
        {
            if (room.ActiveStreams != 0 || room.ActiveSubscriptions != 0)
            {
                return false;
            }
            var session = (await fixture.SessionsAsync()).FirstOrDefault(item => item is { State: "Completed", EndedAtUtc: not null });
            if (session is null || (await fixture.SegmentsAsync()).Any(segment => segment.SessionId == session.Id && !segment.IsClosed))
            {
                return false;
            }
            var manifest = await ReadManifestAsync(fixture, session);
            return manifest.Session.State == "Completed" && manifest.Session.EndedAtUtc == session.EndedAtUtc;
        }, "inactivity completes the session and releases media and events");
    }

    private static async Task AdvanceUntilAsync(ManualRecordingTimeProvider clock, Func<Task<bool>> predicate, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await predicate())
            {
                return;
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100);
        }
        Assert.Fail("Timed out: " + description);
    }

    private static async Task<SessionManifest> ReadManifestAsync(RecordingFixture fixture, BroadcastSession session)
    {
        var path = Path.Combine(MediaPaths.Resolve(fixture.StoragePath, session.DirectoryPath), "场次.json");
        return JsonSerializer.Deserialize<SessionManifest>(await File.ReadAllTextAsync(path), JsonFiles.Options)!;
    }

    private static async Task<long> FirstSourceKeyframeAsync(RecordingFixture fixture, BroadcastSession session, RecordingSegment segment)
    {
        var indexPath = Path.Combine(MediaPaths.Resolve(fixture.StoragePath, session.DirectoryPath), segment.IndexRelativePath);
        foreach (var line in await File.ReadAllLinesAsync(indexPath))
        {
            using var entry = JsonDocument.Parse(line);
            if (entry.RootElement.GetProperty("kind").GetString() == "Keyframe")
            {
                return entry.RootElement.GetProperty("originalTimestampMs").GetInt64();
            }
        }
        Assert.Fail("A real FLV archive must retain its first source keyframe.");
        return -1;
    }
}
