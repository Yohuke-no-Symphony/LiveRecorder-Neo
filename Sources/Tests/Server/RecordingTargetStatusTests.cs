using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>Checks recording target metadata and status parity across the remote capture boundary.</summary>
public sealed class RecordingTargetStatusTests
{
    /// <summary>A successful remote room check refreshes profile data and invalidates a changed avatar cache.</summary>
    [Fact]
    public async Task FreshRoomMetadataUpdatesTargetAndInvalidatesChangedAvatar()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var target = new Streamer { RoomId = 101, Name = "旧昵称", AvatarUrl = "https://example.test/old.png", AvatarRelativePath = "old-avatar.png" };
        await fixture.Mongo.Collection<Streamer>("streamers").InsertOneAsync(target);
        var checkedAt = DateTime.UtcNow.AddSeconds(-1);
        var service = new RecordingTargetStatusService(fixture.Mongo, fixture.Publisher);
        await service.ApplyAsync(new() { TargetId = target.Id, RoomId = target.RoomId }, new()
        {
            State = "Recording",
            Room = new() { RoomId = 101, Uid = 42, Name = "新昵称", AvatarUrl = "https://example.test/new.png", IsLive = true, CheckedAtUtc = checkedAt }
        }, CancellationToken.None);
        var saved = await fixture.Mongo.Collection<Streamer>("streamers").Find(value => value.Id == target.Id).SingleAsync();
        Assert.Equal("新昵称", saved.Name);
        Assert.Equal(42, saved.Uid);
        Assert.Null(saved.AvatarRelativePath);
        Assert.Equal("https://example.test/new.png", saved.AvatarUrl);
        Assert.True(saved.IsLive);
        Assert.Equal(checkedAt.Ticks / TimeSpan.TicksPerMillisecond, saved.LastCheckedAtUtc!.Value.Ticks / TimeSpan.TicksPerMillisecond);
        Assert.Equal("Recording", saved.State);
    }

    /// <summary>A stale or missing room snapshot does not turn a heartbeat into a fresh platform check.</summary>
    [Fact]
    public async Task HeartbeatDoesNotOverwriteFresherRoomMetadata()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var checkedAt = DateTime.UtcNow.AddSeconds(-1);
        var target = new Streamer { RoomId = 101, Name = "最新昵称", AvatarUrl = "https://example.test/current.png", AvatarRelativePath = "current.png", LastCheckedAtUtc = checkedAt };
        await fixture.Mongo.Collection<Streamer>("streamers").InsertOneAsync(target);
        var assignment = new RecordingAssignmentRecord { TargetId = target.Id, RoomId = target.RoomId };
        var service = new RecordingTargetStatusService(fixture.Mongo, fixture.Publisher);
        await service.ApplyAsync(assignment, new()
        {
            State = "StorageFull",
            Room = new() { RoomId = 101, Uid = 42, Name = "旧昵称", AvatarUrl = "https://example.test/old.png", CheckedAtUtc = checkedAt.AddMinutes(-1) }
        }, CancellationToken.None);
        await service.ApplyAsync(assignment, new() { State = "Retrying", ErrorCode = "RECORDING_CAPTURE_FAILED", ErrorArguments = ["source"] }, CancellationToken.None);
        var saved = await fixture.Mongo.Collection<Streamer>("streamers").Find(value => value.Id == target.Id).SingleAsync();
        Assert.Equal("最新昵称", saved.Name);
        Assert.Equal("current.png", saved.AvatarRelativePath);
        Assert.Equal(checkedAt.Ticks / TimeSpan.TicksPerMillisecond, saved.LastCheckedAtUtc!.Value.Ticks / TimeSpan.TicksPerMillisecond);
        Assert.Equal("Retrying", saved.State);
        Assert.Equal("RECORDING_CAPTURE_FAILED", saved.ErrorCode);
        Assert.Equal(["source"], saved.ErrorArguments);
    }
}
