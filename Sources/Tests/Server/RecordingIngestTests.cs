using System.Security.Cryptography;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>Checks coherent recording commits, final acknowledgements and retirement boundaries.</summary>
public sealed class RecordingIngestTests
{
    /// <summary>Uploading files alone exposes no session; a coherent commit makes only the committed media visible.</summary>
    [Fact]
    public async Task CoherentCommitPublishesAndDuplicateCommitIsIdempotent()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var commit = await fixture.UploadAsync(false);
        Assert.Empty(await fixture.Fixture.Mongo.Collection<BroadcastSession>("sessions").Find(_ => true).ToListAsync());
        var ack = await fixture.CommitAsync(commit);
        Assert.Equal(commit.CommitSequence, ack.CommitSequence);
        Assert.Equal(ack, await fixture.CommitAsync(commit));
        var segment = await fixture.Fixture.Mongo.Collection<RecordingSegment>("segments").Find(_ => true).SingleAsync();
        Assert.Equal(commit.MediaCommittedBytes, segment.CommittedBytes);
        Assert.False(segment.IsClosed);
        Assert.Null((await fixture.Fixture.Mongo.Collection<BroadcastSession>("sessions").Find(_ => true).SingleAsync()).EndedAtUtc);
    }

    /// <summary>A persisted commit interrupted before initial library publication is recovered before returning an acknowledgement.</summary>
    [Fact]
    public async Task CommitReceiptBeforeInitialPublicationRecoversOnRetry()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var commit = await fixture.UploadAsync(false);
        var session = await fixture.Fixture.Mongo.Collection<RecordingSessionReceipt>("recordingSessions").Find(_ => true).SingleAsync();
        var canonicalIndex = Path.Combine(fixture.Fixture.Root, session.DirectoryPath, "录像", commit.SegmentId + ".index.jsonl");
        File.Copy(Path.Combine(fixture.Fixture.Root, ".work", "recording", fixture.Lease.AssignmentId, session.Id,
            commit.SegmentId, "index.upload"), canonicalIndex);
        await fixture.Fixture.Mongo.Collection<RecordingSegmentReceipt>("recordingSegments").UpdateOneAsync(value => value.Id == commit.SegmentId,
            Builders<RecordingSegmentReceipt>.Update.Set(value => value.Commit, commit));
        using var restarted = new RecordingIngestService(fixture.Fixture.Mongo, fixture.Fixture.Paths, fixture.Fixture.Settings,
            fixture.Ownership, fixture.Fixture.Publisher);
        await restarted.CommitAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token, commit, CancellationToken.None);
        Assert.Single(await fixture.Fixture.Mongo.Collection<BroadcastSession>("sessions").Find(_ => true).ToListAsync());
        Assert.Single(await fixture.Fixture.Mongo.Collection<RecordingSegment>("segments").Find(_ => true).ToListAsync());
        Assert.True((await fixture.Fixture.Mongo.Collection<RecordingSessionReceipt>("recordingSessions").Find(_ => true).SingleAsync()).LibraryPublished);
    }

    /// <summary>Enabling event capture updates the existing library projection and stale descriptors cannot disable it again.</summary>
    [Fact]
    public async Task EnablingDanmuUpdatesExistingSessionAndRequiresItsFinalSeal()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync(false);
        var commit = await fixture.UploadAsync(true);
        await fixture.CommitAsync(commit);
        var updated = fixture.Session with { HasDanmu = true };
        await fixture.Ingest.RegisterSessionAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation,
            fixture.Lease.Token, updated, CancellationToken.None);
        await fixture.Ingest.RegisterSessionAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation,
            fixture.Lease.Token, fixture.Session, CancellationToken.None);
        Assert.True((await fixture.Fixture.Mongo.Collection<BroadcastSession>("sessions").Find(_ => true).SingleAsync()).HasDanmu);
        var missingEvents = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Ingest.RegisterSessionAsync(fixture.WorkerId,
            fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token,
            fixture.Session with { IsClosed = true, EndedAtUtc = fixture.Session.StartedAtUtc.AddSeconds(1) }, CancellationToken.None));
        Assert.Equal(409, missingEvents.StatusCode);
    }

    /// <summary>A prefix cannot expand its metadata boundary into already uploaded media tags.</summary>
    [Fact]
    public async Task RejectsPrefixWhoseTagHeaderDiffersFromUploadedSource()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var commit = await fixture.UploadAsync(false);
        var prefix = new byte[33];
        RecordingIngestTestContext.Prefix(1).AsSpan(0, 28).CopyTo(prefix);
        prefix[16] = 5;
        prefix[32] = 16;
        var hash = Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant();
        await using var input = new MemoryStream(prefix);
        await fixture.Ingest.UploadAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token,
            fixture.Session.SessionId, commit.SegmentId, "prefix", hash, 0, prefix.Length, input, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.CommitAsync(commit with { PrefixLength = prefix.Length, PrefixSha256 = hash }));
        Assert.Equal(422, error.StatusCode);
        Assert.Empty(await fixture.Fixture.Mongo.Collection<BroadcastSession>("sessions").Find(_ => true).ToListAsync());
    }

    /// <summary>Final session seal prunes staging yet its immutable receipt can answer lost-ACK retries.</summary>
    [Fact]
    public async Task SessionSealCleansStagingAndKeepsVirtualOffsets()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var commit = await fixture.UploadAsync(true);
        await fixture.CommitAsync(commit);
        await fixture.SealSessionAsync();
        Assert.False(Directory.Exists(Path.Combine(fixture.Fixture.Root, ".work", "recording", fixture.Lease.AssignmentId, fixture.Session.SessionId)));
        Assert.Equal(commit.IndexCommittedBytes, await fixture.Ingest.GetOffsetAsync(fixture.WorkerId, fixture.Lease.AssignmentId,
            fixture.Lease.Generation, fixture.Lease.Token, fixture.Session.SessionId, commit.SegmentId, "index", null, commit.IndexCommittedBytes, CancellationToken.None));
        Assert.Equal(commit.PrefixLength, await fixture.Ingest.GetOffsetAsync(fixture.WorkerId, fixture.Lease.AssignmentId,
            fixture.Lease.Generation, fixture.Lease.Token, fixture.Session.SessionId, commit.SegmentId, "prefix", commit.PrefixSha256, commit.PrefixLength, CancellationToken.None));
        Assert.True((await fixture.CommitAsync(commit)).IsClosed);
    }

    /// <summary>Late idempotent upload confirmations do not resurrect a permanently deleted recording.</summary>
    [Fact]
    public async Task PurgedSessionCannotBeResurrectedByHistoricalCommitOrSessionSeal()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var commit = await fixture.UploadAsync(true);
        await fixture.CommitAsync(commit);
        await fixture.SealSessionAsync();
        var trash = new SessionTrashService(fixture.Fixture.Mongo, fixture.Fixture.Settings, fixture.Fixture.Paths);
        await trash.TrashAsync(fixture.Session.SessionId, "admin");
        var entry = await fixture.Fixture.Mongo.Collection<TrashEntry>("trash").Find(value => value.EntityId == fixture.Session.SessionId).SingleAsync();
        await trash.PurgeAsync(entry);
        Assert.True((await fixture.CommitAsync(commit)).IsClosed);
        await fixture.Ingest.RegisterSessionAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token,
            fixture.Session, CancellationToken.None);
        Assert.Empty(await fixture.Fixture.Mongo.Collection<BroadcastSession>("sessions").Find(_ => true).ToListAsync());
        Assert.Empty(await fixture.Fixture.Mongo.Collection<RecordingSegment>("segments").Find(_ => true).ToListAsync());
        await using var late = new MemoryStream(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Ingest.UploadAsync(fixture.WorkerId, fixture.Lease.AssignmentId,
            fixture.Lease.Generation, fixture.Lease.Token, fixture.Session.SessionId, commit.SegmentId, "media", null, 0, commit.MediaCommittedBytes,
            late, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(fixture.Fixture.Root, "*.flv", SearchOption.AllDirectories));
    }

    /// <summary>A stop request cannot release a room before every session and event archive has been sealed.</summary>
    [Fact]
    public async Task StopRequiresDrainBeforeReleasingRoom()
    {
        await using var fixture = await RecordingIngestTestContext.CreateAsync();
        var commit = await fixture.UploadAsync(true);
        await fixture.CommitAsync(commit);
        await fixture.Fixture.Mongo.Collection<Streamer>("streamers").UpdateOneAsync(value => value.Id == fixture.Target.Id,
            Builders<Streamer>.Update.Set(value => value.Enabled, false));
        var stop = new RecordingStopAcknowledgement { SessionIds = [fixture.Session.SessionId] };
        var pending = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Ingest.StopAsync(fixture.WorkerId,
            fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token, stop, CancellationToken.None));
        Assert.Equal(409, pending.StatusCode);
        await fixture.SealSessionAsync();
        await fixture.Ingest.StopAsync(fixture.WorkerId, fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token, stop, CancellationToken.None);
        Assert.Equal("Released", (await fixture.Ownership.GetAsync(fixture.Target.RoomId))!.State);
        Assert.True((await fixture.CommitAsync(commit)).IsClosed);
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Scheduler.GetAssignmentAsync(fixture.WorkerId,
            fixture.Lease.AssignmentId, fixture.Lease.Generation, fixture.Lease.Token, CancellationToken.None));
    }
}
