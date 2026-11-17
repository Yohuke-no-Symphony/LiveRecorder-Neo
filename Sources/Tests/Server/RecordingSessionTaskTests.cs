using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Recording.Contracts;
using Xunit;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>Verifies monitoring observes local broadcasts without publishing uncommitted media or claiming completion.</summary>
public sealed class RecordingSessionTaskTests
{
    /// <summary>既有采集进度不能通过后续心跳改绑到另一个逻辑场次。</summary>
    [Fact]
    public async Task RejectsChangedLogicalSessionIdentity()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var service = new RecordingSessionTaskService(fixture.Fixture.Mongo, fixture.Ownership);
        var progress = CreateProgress(fixture, Guid.NewGuid().ToString("N"));
        progress = progress with { Descriptor = progress.Descriptor with
        {
            LogicalSessionId = progress.Descriptor.SessionId, LogicalStartedAtUtc = progress.Descriptor.StartedAtUtc
        } };
        await service.ReportAsync(fixture.WorkerId, [progress], CancellationToken.None);

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.ReportAsync(fixture.WorkerId,
            [progress with { Descriptor = progress.Descriptor with { LogicalSessionId = "other-logical-session" } }], CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
    }

    /// <summary>A new local broadcast is observable before its first upload without registering an ingest or library session.</summary>
    [Fact]
    public async Task HeartbeatPersistsEachBroadcastBeforeMediaArrives()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var progress = CreateProgress(fixture, Guid.NewGuid().ToString("N"));
        var previous = CreateProgress(fixture, Guid.NewGuid().ToString("N")) with
        {
            CaptureState = "Closed", UploadState = "Queued",
            Descriptor = progress.Descriptor with
            {
                SessionId = Guid.NewGuid().ToString("N"), IsClosed = true, EndedAtUtc = DateTime.UtcNow
            }
        };

        await new RecordingSessionTaskService(fixture.Fixture.Mongo, fixture.Ownership)
            .ReportAsync(fixture.WorkerId, [progress, previous], CancellationToken.None);

        var snapshots = await fixture.Fixture.Mongo.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").Find(_ => true).ToListAsync();
        Assert.Equal(2, snapshots.Count);
        Assert.Contains(snapshots, value => value.Id == progress.Descriptor.SessionId && value.CaptureState == "Recording");
        Assert.Contains(snapshots, value => value.Id == previous.Descriptor.SessionId && value.CaptureState == "Closed");
        Assert.False(await fixture.Fixture.Mongo.Collection<RecordingSessionReceipt>("recordingSessions")
            .Find(value => value.Id == progress.Descriptor.SessionId || value.Id == previous.Descriptor.SessionId).AnyAsync());
        Assert.False(await fixture.Fixture.Mongo.Collection<BroadcastSession>("sessions").Find(_ => true).AnyAsync());
    }

    /// <summary>Omission from the next bounded heartbeat retains prior observations and their last-seen timestamps.</summary>
    [Fact]
    public async Task OmittedBroadcastDoesNotDisappearOrBecomeCompleted()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var service = new RecordingSessionTaskService(fixture.Fixture.Mongo, fixture.Ownership);
        var progress = CreateProgress(fixture, Guid.NewGuid().ToString("N"));
        await service.ReportAsync(fixture.WorkerId, [progress], CancellationToken.None);
        var before = await fixture.Fixture.Mongo.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").Find(_ => true).SingleAsync();

        await service.ReportAsync(fixture.WorkerId, [], CancellationToken.None);

        var after = await fixture.Fixture.Mongo.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").Find(_ => true).SingleAsync();
        Assert.Equal(before.LastReportedAtUtc, after.LastReportedAtUtc);
        Assert.Equal("Recording", after.CaptureState);
        Assert.Equal("CaughtUp", after.UploadState);
    }

    /// <summary>Repeated worker timestamps retain their identity across MongoDB's millisecond date precision.</summary>
    [Fact]
    public async Task RepeatedProgressUpdatesTheExistingBroadcast()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var service = new RecordingSessionTaskService(fixture.Fixture.Mongo, fixture.Ownership);
        var progress = CreateProgress(fixture, Guid.NewGuid().ToString("N"));
        await service.ReportAsync(fixture.WorkerId, [progress], CancellationToken.None);

        await service.ReportAsync(fixture.WorkerId, [progress with { AcknowledgedArchiveBytes = 99, UploadState = "Uploading" }], CancellationToken.None);

        var snapshot = await fixture.Fixture.Mongo.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").Find(_ => true).SingleAsync();
        Assert.Equal(99, snapshot.AcknowledgedArchiveBytes);
        Assert.Equal("Uploading", snapshot.UploadState);
    }

    /// <summary>Authenticated worker identity does not authorize reporting a different room or replacing an established session identity.</summary>
    [Fact]
    public async Task RejectsChangedRoomAndSessionIdentity()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var service = new RecordingSessionTaskService(fixture.Fixture.Mongo, fixture.Ownership);
        var progress = CreateProgress(fixture, Guid.NewGuid().ToString("N"));
        await service.ReportAsync(fixture.WorkerId, [progress], CancellationToken.None);

        var changedRoom = await Assert.ThrowsAsync<InfrastructureException>(() => service.ReportAsync(fixture.WorkerId,
            [progress with { Descriptor = progress.Descriptor with { RoomId = fixture.Target.RoomId + 1 } }], CancellationToken.None));
        var changedIdentity = await Assert.ThrowsAsync<InfrastructureException>(() => service.ReportAsync(fixture.WorkerId,
            [progress with { Descriptor = progress.Descriptor with { LiveTime = progress.Descriptor.LiveTime + 1 } }], CancellationToken.None));

        Assert.Equal(409, changedRoom.StatusCode);
        Assert.Equal(409, changedIdentity.StatusCode);
        var snapshot = await fixture.Fixture.Mongo.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").Find(_ => true).SingleAsync();
        Assert.Equal(progress.Descriptor.RoomId, snapshot.Descriptor.RoomId);
        Assert.Equal(progress.Descriptor.LiveTime, snapshot.Descriptor.LiveTime);
    }

    /// <summary>Publishing the first coherent media commit is distinct from the final successfully sealed archive.</summary>
    [Fact]
    public async Task FinalizedTimestampRequiresFinalSessionSealAndSurvivesRetry()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var commit = await fixture.UploadAsync(true);
        await fixture.CommitAsync(commit);
        var receipts = fixture.Fixture.Mongo.Collection<RecordingSessionReceipt>("recordingSessions");
        Assert.True((await receipts.Find(_ => true).SingleAsync()).Published);
        Assert.Null((await receipts.Find(_ => true).SingleAsync()).FinalizedAtUtc);

        await fixture.SealSessionAsync();
        var finalized = (await receipts.Find(_ => true).SingleAsync()).FinalizedAtUtc;
        Assert.NotNull(finalized);
        await fixture.Ingest.RegisterSessionAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation,
            fixture.Lease.Token, fixture.Session, CancellationToken.None);
        Assert.Equal(finalized, (await receipts.Find(_ => true).SingleAsync()).FinalizedAtUtc);
    }

    private static RecordingSessionProgress CreateProgress(RecordingIngestTestContext fixture, string sessionId) => new()
    {
        AssignmentId = fixture.Lease.AssignmentId, Generation = fixture.Lease.Generation, Token = fixture.Lease.Token,
        Descriptor = fixture.Session with { SessionId = sessionId }, CaptureState = "Recording", UploadState = "CaughtUp",
        CommittedArchiveBytes = 100, AcknowledgedArchiveBytes = 100, CapturedDurationMs = 1000
    };
}
