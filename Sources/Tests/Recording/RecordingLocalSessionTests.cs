using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Recording.Contracts;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证本地采集暂停、事件观察和持久截止时间共享同一个逻辑直播。</summary>
public sealed class RecordingLocalSessionTests
{
    /// <summary>禁用再启用只更换物理采集，不拆分还未下播或超时的逻辑直播。</summary>
    [RecordingFact]
    public async Task DisableAndResumeKeepOneLogicalSessionAndIndependentCaptures()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var (target, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        var original = Assert.Single(await fixture.SessionsAsync());

        await fixture.Context.Collection<Streamer>("streamers").UpdateOneAsync(value => value.Id == target.Id,
            Builders<Streamer>.Update.Set(value => value.Enabled, false));
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && room.ActiveSubscriptions == 0
            && (await fixture.SessionsAsync()).All(value => value.State == "Waiting"), "disabled capture is drained");
        Assert.Null(Assert.Single(await fixture.SessionsAsync()).EndedAtUtc);
        room.LiveStartedAtUtc = room.LiveStartedAtUtc.AddHours(1);
        await fixture.Context.Collection<Streamer>("streamers").UpdateOneAsync(value => value.Id == target.Id,
            Builders<Streamer>.Update.Set(value => value.Enabled, true));
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 1 && room.Connections == 2
            && (await fixture.SessionsAsync()).All(value => value.State == "Recording"), "same logical broadcast resumes");

        var resumed = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(original.Id, resumed.Id);
        Assert.Null(resumed.EndedAtUtc);
        Assert.Equal(2, await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
            .CountDocumentsAsync(value => value.LogicalSessionId == original.Id));
        await fixture.StopSupervisorAsync();
        await fixture.AssertCommittedFilesAsync(true);
    }

    /// <summary>关闭弹幕归档也继续处理上下播事件，且事件回调不会等待自身退出。</summary>
    [RecordingFact]
    public async Task SignalsAreObservedWithoutArchivingDanmu()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var settings = await fixture.Settings.GetAsync();
        settings.RecordDanmu = false;
        await fixture.Settings.SaveAsync(settings);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        var original = Assert.Single(await fixture.SessionsAsync());
        room.QueryFailure = new HttpRequestException("Observe lifecycle through events.");
        room.SendSignal("PREPARING");
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(value => value.Lifecycle?.OfflineSinceUtc is not null), "PREPARING is saved without danmu archive");
        Assert.Equal(1, room.ActiveSubscriptions);
        room.SendSignal("LIVE");
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(value => value.Lifecycle is { LastObservedIsLive: true, OfflineSinceUtc: null }), "LIVE clears the pending offline grace");

        var resumed = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(original.Id, resumed.Id);
        Assert.Null(resumed.EndedAtUtc);
        Assert.False(Directory.Exists(Path.Combine(fixture.StoragePath, resumed.DirectoryPath, "弹幕")));
        await fixture.StopSupervisorAsync();
    }

    /// <summary>状态接口挂起时，准备消息仍能停止媒体而不会等待挂起的查询。</summary>
    [RecordingFact]
    public async Task PreparingSignalStopsMediaWhileRoomLookupIsBlocked()
    {
        await using var fixture = await RecordingFixture.CreateAsync(30);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.BlockNextQuery();
        await room.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        room.IsLive = false;

        room.SendSignal("PREPARING");
        await RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 0 && room.ActiveSubscriptions == 1
            && (await fixture.SessionsAsync()).Any(value => value.Lifecycle?.OfflineSinceUtc is not null), "PREPARING cancels the blocked media monitor");

        Assert.False(room.ReleaseQuery.Task.IsCompleted);
        Assert.Null(Assert.Single(await fixture.SessionsAsync()).EndedAtUtc);
        await fixture.StopSupervisorAsync();
    }

    /// <summary>主站停机时间计入无媒体上限，过期原场不会被重新拉流。</summary>
    [RecordingFact]
    public async Task RestartAfterMediaDeadlineClosesThePersistedLogicalSession()
    {
        var clock = new ManualRecordingTimeProvider();
        await using var fixture = await RecordingFixture.CreateAsync(30, timeProvider: clock);
        var settings = await fixture.Settings.GetAsync();
        settings.StreamDisconnectTimeoutMinutes = 1;
        await fixture.Settings.SaveAsync(settings);
        var (_, room) = await fixture.AddRoomAsync(100);
        await fixture.StartSupervisorAsync();
        await WaitForMediaAsync(fixture, room);
        room.PauseMedia();
        await room.MediaPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.StopSupervisorAsync();
        var original = Assert.Single(await fixture.SessionsAsync());
        Assert.NotNull(original.LastMediaReceivedAtUtc);
        room.QueryFailure = new HttpRequestException("Room unavailable while restarting.");
        clock.Advance(TimeSpan.FromMinutes(2));

        await fixture.StartSupervisorAsync();
        await RecordingFixture.WaitUntilAsync(async () => (await fixture.SessionsAsync()).Any(value => value.Id == original.Id && value.State == "Completed"), "restart observes the expired durable deadline");

        var completed = Assert.Single(await fixture.SessionsAsync());
        Assert.Equal(original.Id, completed.Id);
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, completed.Lifecycle!.EndReason);
        Assert.Equal(1, room.Connections);
        await fixture.StopSupervisorAsync();
    }

    private static Task WaitForMediaAsync(RecordingFixture fixture, ControlledRoom room) =>
        RecordingFixture.WaitUntilAsync(async () => room.ActiveStreams == 1 && room.ActiveSubscriptions == 1
            && (await fixture.SegmentsAsync()).Any(value => value.DurationMs > 1000)
            && (await fixture.SessionsAsync()).Any(value => value.TotalBytes > 0), "local media and lifecycle listener are active");
}
