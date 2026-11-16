using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using System.Text.Json;
using Yohuke.DanmuCore.Models.Accounts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>Verifies durable room exclusivity, disconnected reservations and explicit drained ownership release.</summary>
public sealed class RecordingOwnershipTests
{
    /// <summary>Independent coordinators cannot reserve the same room locally and remotely at the same time.</summary>
    [Fact]
    public async Task ConcurrentLocalAndRemoteAcquisitionHasOneDurableWinner()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var target = new Streamer { RoomId = 111 };
        var secondPaths = new AppPaths(Path.Combine(fixture.DirectoryPath, "other-data"), Path.Combine(fixture.DirectoryPath, "other-records"));
        secondPaths.EnsureDirectories();
        var first = new RecordingOwnershipService(fixture.Mongo, fixture.Paths, new JobTestSecretProtector());
        var second = new RecordingOwnershipService(fixture.Mongo, secondPaths, new JobTestSecretProtector());
        var attempts = await Task.WhenAll(first.TryAcquireLocalAsync(target, "local-instance", CancellationToken.None),
            second.TryAcquireWorkerAsync(target, "worker-instance", CancellationToken.None));
        Assert.Single(attempts, value => value is not null);
        Assert.NotNull(await first.GetAsync(target.RoomId));
    }

    /// <summary>Process recreation and an arbitrarily old last-seen timestamp do not release remote recording authority.</summary>
    [Fact]
    public async Task DisconnectedReservationSurvivesCoordinatorRecreationWithoutTimeoutTakeover()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        await fixture.Fixture.Mongo.Collection<RecordingOwnership>("recordingOwnerships").UpdateOneAsync(value => value.RoomId == fixture.Target.RoomId,
            Builders<RecordingOwnership>.Update.Set(value => value.LastSeenAtUtc, DateTime.UtcNow.AddYears(-1)));
        var restarted = new RecordingOwnershipService(fixture.Fixture.Mongo, fixture.Fixture.Paths, new JobTestSecretProtector());
        Assert.Null(await restarted.TryAcquireLocalAsync(fixture.Target, "new-main-instance", CancellationToken.None));
        Assert.Equal(fixture.Lease.AssignmentId, (await restarted.GetAsync(fixture.Target.RoomId))!.AssignmentId);
    }

    /// <summary>A stable local installation can resume its reserved room while a different owner cannot steal it.</summary>
    [Fact]
    public async Task LocalOwnerResumesWithStableIdentity()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var target = new Streamer { RoomId = 222 };
        var service = new RecordingOwnershipService(fixture.Mongo, fixture.Paths, new JobTestSecretProtector());
        var first = await service.TryAcquireLocalAsync(target, "stable-installation", CancellationToken.None);
        var restarted = new RecordingOwnershipService(fixture.Mongo, fixture.Paths, new JobTestSecretProtector());
        Assert.Equal(first!.Id, (await restarted.TryAcquireLocalAsync(target, "stable-installation", CancellationToken.None))!.Id);
        Assert.Null(await restarted.TryAcquireLocalAsync(target, "other-installation", CancellationToken.None));
    }

    /// <summary>Orphan assignment documents cannot consume capacity or appear as a real room reservation.</summary>
    [Fact]
    public async Task AssignmentWithoutCurrentRoomReferenceIsNotDispatched()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        await fixture.Fixture.Mongo.Collection<RecordingAssignmentRecord>("recordingAssignments").InsertOneAsync(new()
        {
            OwnerId = fixture.WorkerId, OwnerKind = "Worker", RoomId = 999, Generation = 1
        });
        Assert.Single(await fixture.Ownership.ListWorkerAsync(fixture.WorkerId, CancellationToken.None));
        Assert.Single((await fixture.HeartbeatAsync()).RecordingAssignments);
    }

    /// <summary>Disabling a recording node retains its reservation and authentication until the spool has drained.</summary>
    [Fact]
    public async Task DisabledNodeCanDrainButCannotBeDeletedOrReadFreshCredentialsAfterRelease()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var worker = await fixture.Workers.RequireAsync(fixture.WorkerId, CancellationToken.None);
        await fixture.Workers.UpdateAsync(worker.Id, worker.Name, false, worker.Configuration, worker.ConfigurationRevision, CancellationToken.None);
        Assert.True(await fixture.Workers.AuthenticateAsync(worker.Id, fixture.Secret, CancellationToken.None));
        Assert.False(Assert.Single((await fixture.HeartbeatAsync()).RecordingAssignments).Enabled);
        var rejected = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Workers.DeleteAsync(worker.Id, CancellationToken.None));
        Assert.Equal(409, rejected.StatusCode);
        var descriptor = await fixture.Scheduler.GetAssignmentAsync(worker.Id, fixture.Lease.AssignmentId, fixture.Lease.Generation,
            fixture.Lease.Token, CancellationToken.None);
        Assert.False(descriptor.Enabled);
        Assert.Empty(descriptor.Accounts);
        Assert.Equal("Owned", (await fixture.Ownership.GetAsync(fixture.Target.RoomId))!.State);
    }

    /// <summary>Long-lived rooms above a reduced concurrency limit receive explicit drained-stop controls.</summary>
    [Fact]
    public async Task LowerConcurrencyStopsOnlyExcessReservations()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        await fixture.Fixture.Mongo.Collection<Streamer>("streamers").InsertOneAsync(new() { RoomId = 902, Name = "second" });
        Assert.Equal(2, (await fixture.HeartbeatAsync()).RecordingAssignments.Count);
        var worker = await fixture.Workers.RequireAsync(fixture.WorkerId, CancellationToken.None);
        var updated = await fixture.Workers.UpdateAsync(worker.Id, worker.Name, true,
            worker.Configuration with { MaxConcurrentEncodes = 1 }, worker.ConfigurationRevision, CancellationToken.None);
        var state = await fixture.Scheduler.HeartbeatAsync(worker.Id, new()
        {
            AppliedConfigurationRevision = updated.ConfigurationRevision, Telemetry = new() { FreeBytes = long.MaxValue }
        }, CancellationToken.None);
        Assert.Single(state.RecordingAssignments, value => value.Enabled);
        Assert.Single(state.RecordingAssignments, value => !value.Enabled);
        Assert.True(state.RecordingAssignments[0].Enabled);
    }

    /// <summary>Decrypted capture cookies are returned only transiently and old control credentials cannot fetch them after rotation.</summary>
    [Fact]
    public async Task AssignmentCredentialsAreNotPersistedAndRemainBoundToCurrentWorkerSecret()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        const string COOKIE = "SESSDATA=test-secret;bili_jct=csrf;";
        await fixture.Fixture.Mongo.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(new()
        {
            Uid = 42, ProtectedCredentials = JsonSerializer.Serialize(new BilibiliLoginCredentials { Cookie = COOKIE })
        });
        var assignment = await fixture.Scheduler.GetAssignmentAsync(fixture.WorkerId, fixture.Lease.AssignmentId,
            fixture.Lease.Generation, fixture.Lease.Token, CancellationToken.None, fixture.Secret);
        Assert.Equal(COOKIE, Assert.Single(assignment.Accounts).Cookie);
        var stored = await fixture.Fixture.Mongo.Collection<RecordingAssignmentRecord>("recordingAssignments")
            .Find(value => value.Id == fixture.Lease.AssignmentId).SingleAsync();
        Assert.Empty(stored.Definition!.Accounts);
        Assert.Empty(stored.Definition.Token);
        await fixture.Workers.RotateAsync(fixture.WorkerId, CancellationToken.None);
        var rejected = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Scheduler.GetAssignmentAsync(fixture.WorkerId,
            fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token, CancellationToken.None, fixture.Secret));
        Assert.Equal(401, rejected.StatusCode);
    }

    /// <summary>A room nickname refresh does not restart an otherwise unchanged long-running capture assignment.</summary>
    [Fact]
    public async Task NicknameMetadataChangeDoesNotReviseCapturePolicy()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var before = Assert.Single((await fixture.HeartbeatAsync()).RecordingAssignments);
        await fixture.Fixture.Mongo.Collection<Streamer>("streamers").UpdateOneAsync(value => value.Id == fixture.Target.Id,
            Builders<Streamer>.Update.Set(value => value.Name, "平台新昵称"));
        var after = Assert.Single((await fixture.HeartbeatAsync()).RecordingAssignments);
        Assert.Equal(before.AssignmentId, after.AssignmentId);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.ConfigurationRevision, after.ConfigurationRevision);
    }
}
