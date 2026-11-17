using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Tasks;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证主站本机录像以场次关联事件，并将日志故障隔离于录制生命周期。</summary>
public sealed class RecordingTaskLogTests
{
    /// <summary>正常采集、断流重试和超时封口均记录到正确场次，超时作为信息事件保留。</summary>
    [RecordingFact]
    public async Task LocalCaptureAndClosureEventsStayWithinTheirSessionAndStage()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await CreateFixtureAsync(clock);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture);
        var session = Assert.Single(await fixture.SessionsAsync());
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.Logs.ReadTaskAsync("local", session.Id, null, "Capturing", null)).Entries.Count >= 2,
            "published session and active media have persistent events");
        room.QueryFailure = new HttpRequestException("Network unavailable");
        room.StreamFailure = new HttpRequestException("Network unavailable");
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && (await fixture.SegmentsAsync()).All(segment => segment.IsClosed),
            "disconnected media closes its current segment");
        clock.Advance(TimeSpan.FromMinutes(1));
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(value => value.EndedAtUtc is not null), "silence seals the session");
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.Logs.ReadTaskAsync("local", session.Id, null, "Uploading", null)).Entries.Count > 0,
            "local archive completion is recorded");

        var capture = await fixture.Logs.ReadTaskAsync("local", session.Id, null, "Capturing", null);
        Assert.All(capture.Entries, entry =>
        {
            Assert.Equal(session.Id, entry.JobId);
            Assert.Equal("Capturing", entry.Stage);
            Assert.Equal("ServerEvent", entry.Source);
        });
        Assert.Contains(capture.Entries, entry => entry.Level == "Information" && entry.Message.Contains("room monitoring will continue", StringComparison.Ordinal));
        Assert.Contains(capture.Entries, entry => entry.Level == "Information" && entry.Message.Contains("archive is closed", StringComparison.Ordinal));
        var upload = await fixture.Logs.ReadTaskAsync("local", session.Id, null, "Uploading", null);
        Assert.Contains(upload.Entries, entry => entry.Message.Contains("no worker upload is required", StringComparison.Ordinal));
        var unrelated = await fixture.Logs.ReadTaskAsync("local", "another-session", null, "Capturing", null);
        Assert.Empty(unrelated.Entries);
        await fixture.StopSupervisorAsync();
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>日志数据库失败及日志内部取消不阻止超时归档，也不让录像目标停止监控。</summary>
    [RecordingFact]
    public async Task TaskLogFailuresAndCancellationCannotPreventTimeoutClosure()
    {
        foreach (var failure in new Exception[]
        {
            new MongoException("Log persistence unavailable"), new OperationCanceledException("Log operation cancelled"), new TimeoutException("Log selection timed out")
        })
        {
            var clock = new ManualRecordingTimeProvider();
            await using var fixture = await CreateFixtureAsync(clock);
            var (target, room) = await fixture.AddRoomAsync(100);
            using var failedLogs = new TranscodeWorkerLogStore(new RecordingLogFailureMongoContext(failure));
            var events = new TaskEventLogStore(failedLogs, NullLogger<TaskEventLogStore>.Instance);
            await fixture.StartSupervisorAsync(taskLogs: events);
            await WaitForMediaAsync(fixture);
            room.QueryFailure = new HttpRequestException("Network unavailable");
            room.StreamFailure = new HttpRequestException("Network unavailable");
            await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && (await fixture.SegmentsAsync()).All(segment => segment.IsClosed),
                "source failure releases the segment despite log failure");
            clock.Advance(TimeSpan.FromMinutes(1));
            await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(value => value.EndedAtUtc is not null), "log failure does not prevent sealing");

            var session = Assert.Single(await fixture.SessionsAsync());
            Assert.Equal("Completed", session.State);
            Assert.Equal(session.LastMediaAtUtc, session.EndedAtUtc);
            var monitored = await fixture.Context.Collection<Streamer>("streamers").Find(value => value.Id == target.Id).SingleAsync();
            Assert.True(monitored.Enabled);
            Assert.True(monitored.IsMonitored);
            await fixture.StopSupervisorAsync();
            await fixture.AssertCommittedFilesAsync(true);
        }
    }

    /// <summary>同一生命周期事件从Mongo毫秒精度恢复后仍可幂等重放，取消的旁路写入不会抛出。</summary>
    [RecordingFact]
    public async Task ServerEventReplayPreservesIdentityAfterMetadataRoundTrip()
    {
        await using var fixture = await RecordingFixture.CreateAsync(0.2);
        var events = new TaskEventLogStore(fixture.Logs, NullLogger<TaskEventLogStore>.Instance);
        var persistedTimestamp = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var timestamp = persistedTimestamp.AddTicks(123);
        await events.WriteRecordingAsync("local", "session", "Capturing", "Information", "Archive published.", timestamp, "session-start");
        await events.WriteRecordingAsync("local", "session", "Capturing", "Information", "Archive published.", persistedTimestamp, "session-start");
        var page = await fixture.Logs.ReadTaskAsync("local", "session", null, "Capturing", null);
        Assert.Equal(persistedTimestamp, Assert.Single(page.Entries).TimestampUtc);
        var batch = Assert.Single(await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Find(value => value.WorkerId == "local").ToListAsync());
        var replay = new WorkerLogBatch { StreamId = batch.StreamId, BatchId = batch.BatchId, Entries = batch.Entries, DroppedCount = batch.DroppedCount };
        Assert.Equal(batch.PayloadHash, Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(replay, WorkerProtocol.JsonOptions))));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await events.WriteRecordingAsync("local", "session", "Capturing", "Information", "Cancelled log event.", timestamp,
            cancellationToken: cancellation.Token);
        Assert.Single((await fixture.Logs.ReadTaskAsync("local", "session", null, "Capturing", null)).Entries);
    }

    private static async Task<RecordingFixture> CreateFixtureAsync(TimeProvider clock)
    {
        var fixture = await RecordingFixture.CreateAsync(30, timeProvider: clock);
        var settings = await fixture.Settings.GetAsync();
        settings.StreamDisconnectTimeoutMinutes = 1;
        await fixture.Settings.SaveAsync(settings);
        return fixture;
    }

    private static Task WaitForMediaAsync(RecordingFixture fixture) => RecordingFixture.WaitUntilAsync(
        async () => (await fixture.SessionsAsync()).Any(session => session.TotalBytes > 0), "committed media activates the session");
}
