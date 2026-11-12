using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>真实协调器、数据库和 FLV 写入的生命周期回归。</summary>
public sealed class RecordingLifecycleTests
{
    [RecordingFact]
    public async Task SameLiveTimeReconnectsStayInOneSessionAndDanmuOnlyLivesInFiles()
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        var (_, room) = await fixture.AddRoomAsync(100);
        room.DisconnectAfterMs = 1500;
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () =>
        {
            var segments = await fixture.SegmentsAsync();
            return room.Connections >= 3 && segments.Count >= 3 && segments.Count(x => x.IsClosed) >= 2;
        }, "three real FLV connections commit in one live session", 20);
        await fixture.StopSupervisorAsync();
        var session = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(new DateTimeOffset(room.LiveStartedAtUtc).ToUnixTimeSeconds(), session.LiveTime);
        Assert.True((await fixture.SegmentsAsync()).Count >= 3);
        Assert.Equal(0, room.ActiveStreams);
        Assert.Equal(0, room.ActiveSubscriptions);
        await fixture.AssertCommittedFilesAsync(true);
        var eventsDirectory = Path.Combine(MediaPaths.Resolve(fixture.StoragePath, session.DirectoryPath), "弹幕");
        var lines = await File.ReadAllLinesAsync(Path.Combine(eventsDirectory, "事件.jsonl"));
        Assert.NotEmpty(lines);
        var messages = lines.Select(x => JsonSerializer.Deserialize<RecordedEventEnvelope>(x, JsonFiles.Options)!).ToArray();
        Assert.All(messages, message => Assert.Equal("Danmu", message.Kind));
        Assert.Equal(Enumerable.Range(1, messages.Length).Select(x => (long)x), messages.Select(x => x.Sequence));
        using var lease = new FileStream(Path.Combine(eventsDirectory, ".事件.writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var names = await fixture.Context.Database.ListCollectionNames().ToListAsync();
        Assert.DoesNotContain(names, x => x.Contains("event", StringComparison.OrdinalIgnoreCase) || x.Contains("danmu", StringComparison.OrdinalIgnoreCase));
    }

    [RecordingFact]
    public async Task TitleChangesKeepSessionButNewPlatformLiveTimeStartsAnother()
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        var (_, room) = await fixture.AddRoomAsync(100);
        room.DisconnectAfterMs = 1500;
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SegmentsAsync()).Any(x => x.IsClosed), "first source connection closes");
        var original = Assert.Single(await fixture.SessionsAsync());
        room.Title = "同场修改后的标题";
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SegmentsAsync()).Count(x => x.IsClosed) >= 2, "changed title is recorded by second connection");
        Assert.Equal(original.Id, Assert.Single(await fixture.SessionsAsync()).Id);
        room.LiveStartedAtUtc = room.LiveStartedAtUtc.AddHours(1);
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Count == 2 && room.Connections >= 3, "new platform LiveTime creates another session");
        await fixture.StopSupervisorAsync();
        var sessions = await fixture.SessionsAsync();
        Assert.NotNull(sessions.Single(x => x.Id == original.Id).EndedAtUtc);
        Assert.Contains(sessions, x => x.Id != original.Id && x.LiveTime == new DateTimeOffset(room.LiveStartedAtUtc).ToUnixTimeSeconds());
        await fixture.AssertCommittedFilesAsync(true);
    }

    [RecordingFact]
    public async Task DisablingTargetClosesLiveMediaAndDanmuWriters()
    {
        await using var fixture = await RecordingFixture.CreateAsync(12);
        var (target, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 1 && (await fixture.SegmentsAsync()).Any(x => x.DurationMs > 1000), "live media commits while connection stays open");
        await fixture.Context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == target.Id, Builders<Streamer>.Update.Set(x => x.Enabled, false));
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && room.ActiveSubscriptions == 0 && (await fixture.SegmentsAsync()).All(x => x.IsClosed), "disabled target stops both writers", 10);
        var bytes = room.ReadBytes;
        await Task.Delay(1200);
        Assert.Equal(bytes, room.ReadBytes);
        Assert.False((await fixture.Context.Collection<Streamer>("streamers").Find(x => x.Id == target.Id).SingleAsync()).Enabled);
        var ended = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal("Completed", ended.State);
        Assert.NotNull(ended.EndedAtUtc);
        await fixture.AssertCommittedFilesAsync(true);
    }

    [RecordingFact]
    public async Task DisableDuringSlowRoomLookupCannotBeOverwrittenByRuntimeStatus()
    {
        await using var fixture = await RecordingFixture.CreateAsync(12);
        var (target, room) = await fixture.AddRoomAsync(100);
        room.BlockNextQuery();
        await fixture.StartSupervisorAsync();
        await room.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == target.Id, Builders<Streamer>.Update.Set(x => x.Enabled, false).Set(x => x.Quality, 400));
        room.ReleaseQuery.TrySetResult();
        await RecordingFixture.WaitUntilAsync(() => Task.FromResult(room.Connections > 0), "blocked room lookup returns and runtime metadata is saved");
        var saved = await fixture.Context.Collection<Streamer>("streamers").Find(x => x.Id == target.Id).SingleAsync();
        Assert.False(saved.Enabled);
        Assert.Equal(400, saved.Quality);
        await RecordingFixture.WaitUntilAsync(() => Task.FromResult(room.ActiveStreams == 0 && room.ActiveSubscriptions == 0), "supervisor cancels target disabled during lookup", 10);
    }

    [RecordingFact]
    public async Task RestartRestoresManifestAndAppendsWithoutSplittingSameLiveTime()
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        var (_, room) = await fixture.AddRoomAsync(100);
        room.DisconnectAfterMs = 1500;
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SegmentsAsync()).Any(x => x.IsClosed), "first archive is committed");
        await fixture.StopSupervisorAsync();
        var previous = Assert.Single(await fixture.SessionsAsync());
        Assert.Null(previous.EndedAtUtc);
        var oldSegments = await fixture.SegmentsAsync();
        await fixture.Context.Collection<BroadcastSession>("sessions").DeleteManyAsync(FilterDefinition<BroadcastSession>.Empty);
        await fixture.Context.Collection<RecordingSegment>("segments").DeleteManyAsync(FilterDefinition<RecordingSegment>.Empty);
        room.Title = "重启后同场的新标题";
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SegmentsAsync()).Count(x => x.IsClosed) > oldSegments.Count, "manifest restores old records and another source connection appends", 15);
        await fixture.StopSupervisorAsync();
        var recovered = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(previous.Id, recovered.Id);
        Assert.Equal(previous.DirectoryPath, recovered.DirectoryPath);
        var segments = await fixture.SegmentsAsync();
        foreach (var old in oldSegments)
        {
            Assert.Equal(old.CommittedBytes, segments.Single(x => x.Id == old.Id).CommittedBytes);
        }
        await fixture.AssertCommittedFilesAsync(true);
    }

    [RecordingFact]
    public async Task RemovingMonitoringKeepsStreamerHistoryAndCompletesItsCurrentSession()
    {
        await using var fixture = await RecordingFixture.CreateAsync(12);
        var (target, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 1 && (await fixture.SegmentsAsync()).Any(x => x.DurationMs > 1000), "live source commits before monitoring is removed");
        await fixture.Context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == target.Id, Builders<Streamer>.Update.Set(x => x.IsMonitored, false).Set(x => x.Enabled, false));
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && room.ActiveSubscriptions == 0 && (await fixture.SessionsAsync()).All(x => x.State == "Completed"), "soft-removed monitor closes source and current session", 10);
        var preserved = await fixture.Context.Collection<Streamer>("streamers").Find(x => x.Id == target.Id).SingleAsync();
        Assert.False(preserved.IsMonitored);
        Assert.Equal(target.Id, Assert.Single(await fixture.SessionsAsync()).StreamerId);
        await fixture.AssertCommittedFilesAsync(true);
    }
}
