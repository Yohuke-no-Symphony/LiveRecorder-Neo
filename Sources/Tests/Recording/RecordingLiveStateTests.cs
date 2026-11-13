using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证持续拉流期间的下播检测、场次状态和采集任务生命周期。</summary>
public sealed class RecordingLiveStateTests
{
    /// <summary>下播时主动关闭尚未断开的媒体和弹幕连接，并在宽限期同步等待状态。</summary>
    [RecordingFact]
    public async Task OfflineRoomStopsOpenStreamAndPersistsWaitingStateDuringGrace()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var (target, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.IsLive = false;

        await WaitForStateAsync(fixture, room, "Waiting");
        var session = Assert.Single(await fixture.SessionsAsync());
        Assert.Null(session.EndedAtUtc);
        var saved = await fixture.Context.Collection<Streamer>("streamers").Find(x => x.Id == target.Id).SingleAsync();
        Assert.False(saved.IsLive);
        Assert.Equal("Waiting", saved.State);
        Assert.Null(saved.Error);
        Assert.Null(saved.ErrorCode);
        Assert.Equal("Waiting", (await ReadManifestAsync(fixture, session)).Session.State);
        var bytes = room.ReadBytes;
        await Task.Delay(1200);
        Assert.Equal(bytes, room.ReadBytes);
        Assert.Equal(1, room.Connections);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>宽限期内恢复同一开播时间时复用场次，并恢复录制状态。</summary>
    [RecordingFact]
    public async Task SameBroadcastResumesInExistingSessionDuringGrace()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        var original = Assert.Single(await fixture.SessionsAsync());
        room.IsLive = false;
        await WaitForStateAsync(fixture, room, "Waiting");

        room.IsLive = true;
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 1 && room.ActiveSubscriptions == 1 && room.Connections == 2 && (await fixture.SessionsAsync()).All(x => x.State == "Recording"), "same broadcast resumes after going offline", 10);
        var resumed = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(original.Id, resumed.Id);
        Assert.Null(resumed.EndedAtUtc);
        Assert.Equal("Recording", (await ReadManifestAsync(fixture, resumed)).Session.State);
        await fixture.StopSupervisorAsync();
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>宽限期到期后结束场次，最终清单和媒体分段均已关闭。</summary>
    [RecordingFact]
    public async Task OfflineRoomCompletesSessionAfterGrace()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var settings = await fixture.Settings.GetAsync();
        settings.SessionGraceMinutes = 0;
        await fixture.Settings.SaveAsync(settings);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.IsLive = false;

        await WaitForStateAsync(fixture, room, "Completed");
        var session = Assert.Single(await fixture.SessionsAsync());
        Assert.NotNull(session.EndedAtUtc);
        Assert.True(session.EndedAtUtc >= session.LastMediaAtUtc);
        var manifest = await ReadManifestAsync(fixture, session);
        Assert.Equal("Completed", manifest.Session.State);
        Assert.Equal(session.EndedAtUtc, manifest.Session.EndedAtUtc);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>原 CDN 连接未结束时，新的平台开播时间也会结束旧场并启动新场。</summary>
    [RecordingFact]
    public async Task NewPlatformLiveTimeStopsExistingOpenStream()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        var original = Assert.Single(await fixture.SessionsAsync());
        room.LiveStartedAtUtc = room.LiveStartedAtUtc.AddHours(1);

        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 1 && room.Connections == 2 && (await fixture.SessionsAsync()).Count == 2, "new broadcast replaces an open source connection", 10);
        var sessions = await fixture.SessionsAsync();
        var completed = sessions.Single(x => x.Id == original.Id);
        Assert.Equal("Completed", completed.State);
        Assert.NotNull(completed.EndedAtUtc);
        var current = sessions.Single(x => x.Id != original.Id);
        Assert.Equal("Recording", current.State);
        Assert.Equal(new DateTimeOffset(room.LiveStartedAtUtc).ToUnixTimeSeconds(), current.LiveTime);
        Assert.All((await fixture.SegmentsAsync()).Where(x => x.SessionId == original.Id), segment => Assert.True(segment.IsClosed));
        await fixture.StopSupervisorAsync();
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>直播状态接口暂时失败时继续当前有效录制，查询恢复后仍能检测下播。</summary>
    [RecordingFact]
    public async Task TransientRoomLookupFailureDoesNotInterruptActiveStream()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var (target, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        var queries = room.Queries;
        var bytes = room.ReadBytes;
        room.QueryFailure = new HttpRequestException("Temporary room lookup failure");

        await RecordingFixture.WaitUntilAsync(() => Task.FromResult(room.Queries >= queries + 2), "room lookup retries while the source stays connected", 10);
        Assert.Equal(1, room.ActiveStreams);
        Assert.Equal(1, room.Connections);
        Assert.True(room.ReadBytes > bytes);
        var saved = await fixture.Context.Collection<Streamer>("streamers").Find(x => x.Id == target.Id).SingleAsync();
        Assert.Equal("Recording", saved.State);
        Assert.Null(saved.Error);
        room.QueryFailure = null;
        room.IsLive = false;
        await WaitForStateAsync(fixture, room, "Waiting");
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>媒体连接自然结束时取消尚未完成的状态查询，后续连接不被阻塞。</summary>
    [RecordingFact]
    public async Task StreamCompletionCancelsPendingRoomLookup()
    {
        await using var fixture = await RecordingFixture.CreateAsync(5);
        var (_, room) = await fixture.AddRoomAsync(100);
        room.DisconnectAfterMs = 2500;
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(() => Task.FromResult(room.ActiveStreams == 1), "first source connection opens");
        room.BlockNextQuery();
        await room.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await RecordingFixture.WaitUntilAsync(async () => room.Connections >= 2 && (await fixture.SegmentsAsync()).Any(x => x.IsClosed), "source completion cancels the blocked query and allows reconnection", 10);
        Assert.False(room.ReleaseQuery.Task.IsCompleted);
        await fixture.StopSupervisorAsync();
        Assert.Equal(0, room.ActiveStreams);
        Assert.Equal(0, room.ActiveSubscriptions);
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>等待状态已落盘但通知暂时失败时，后续轮询重新完成状态同步。</summary>
    [RecordingFact]
    public async Task OfflineStatePublicationRetriesAfterTransientFailure()
    {
        MongoContext? context = null;
        var waitingPublications = 0;
        var publisher = new CallbackRecordingPublisher(async (kind, id, token) =>
        {
            if (kind != "session")
            {
                return;
            }
            var session = await context!.Collection<BroadcastSession>("sessions").Find(x => x.Id == id).SingleAsync(token);
            if (session.State == "Waiting" && Interlocked.Increment(ref waitingPublications) == 1)
            {
                throw new IOException("Temporary session notification failure");
            }
        });
        await using var fixture = await RecordingFixture.CreateAsync(30, publisher: publisher);
        context = fixture.Context;
        var (target, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.IsLive = false;

        await RecordingFixture.WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref waitingPublications) >= 2), "waiting state notification retries after a temporary failure", 10);
        await WaitForStateAsync(fixture, room, "Waiting");
        var saved = await fixture.Context.Collection<Streamer>("streamers").Find(x => x.Id == target.Id).SingleAsync();
        Assert.Equal("Waiting", saved.State);
        Assert.Null(saved.Error);
        Assert.Equal(1, room.Connections);
        await fixture.AssertCommittedFilesAsync(true);
    }

    private static Task WaitForMediaAsync(RecordingFixture fixture, ControlledRoom room)
    {
        return RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 1 && room.ActiveSubscriptions == 1 && (await fixture.SegmentsAsync()).Any(x => x.DurationMs > 1000), "open media connection commits data", 10);
    }

    private static Task WaitForStateAsync(RecordingFixture fixture, ControlledRoom room, string state)
    {
        return RecordingFixture.WaitUntilAsync(async () =>
        {
            if (room.ActiveStreams != 0 || room.ActiveSubscriptions != 0)
            {
                return false;
            }
            var sessions = await fixture.SessionsAsync();
            if (sessions.Count != 1 || sessions[0].State != state || (await fixture.SegmentsAsync()).Any(x => !x.IsClosed))
            {
                return false;
            }
            return (await ReadManifestAsync(fixture, sessions[0])).Session.State == state;
        }, "offline broadcast stops both writers and updates session state", 10);
    }

    private static async Task<SessionManifest> ReadManifestAsync(RecordingFixture fixture, BroadcastSession session)
    {
        var path = Path.Combine(MediaPaths.Resolve(fixture.StoragePath, session.DirectoryPath), "场次.json");
        return JsonSerializer.Deserialize<SessionManifest>(await File.ReadAllTextAsync(path), JsonFiles.Options)!;
    }
}
