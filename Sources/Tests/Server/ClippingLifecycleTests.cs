using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>Verifies remote clipping source snapshots, execution fencing and shared publication.</summary>
public sealed class ClippingLifecycleTests
{
    /// <summary>Registration prevents a clipping identity from connecting as a transcoding executable.</summary>
    [Fact]
    public async Task WorkerKindMustMatchRegisteredIdentity()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Workers.ConnectAsync(new()
        {
            WorkerId = fixture.WorkerId, Secret = fixture.Secret
        }, CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
        var worker = await fixture.Workers.RequireAsync(fixture.WorkerId, CancellationToken.None);
        Assert.Equal("Clipping", worker.Kind);
        Assert.Equal(0, worker.Configuration.GpuConcurrency);
        Assert.Equal(0, worker.Configuration.CpuConcurrency);
    }

    /// <summary>Local execution defers only indexed jobs while a configured clipping node is available.</summary>
    [Fact]
    public async Task LocalFallbackRemainsAvailableForLegacyInputsAndOfflineWorkers()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (session, segments) = await fixture.Jobs.SeedSessionAsync();
        var job = JobTestContext.CreateJob(session, segments);
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        Assert.Contains(job.Id, await fixture.Scheduler.GetRemoteEligibleJobIdsAsync(CancellationToken.None));
        fixture.Ranges.Available = false;
        fixture.Preparation.Invalidate(job);
        Assert.Empty(await fixture.Scheduler.GetRemoteEligibleJobIdsAsync(CancellationToken.None));
        Assert.Empty((await fixture.HeartbeatAsync()).ClipAssignments);
        fixture.Ranges.Available = true;
        await fixture.Jobs.Mongo.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == fixture.WorkerId,
            Builders<TranscodeWorker>.Update.Set(value => value.LastSeenAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Empty(await fixture.Scheduler.GetRemoteEligibleJobIdsAsync(CancellationToken.None));
    }

    /// <summary>Insufficient workspace and oversized control assignments remain queued for local execution.</summary>
    [Fact]
    public async Task CapacityAndControlFrameLimitsPreserveLocalFallback()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (session, segments) = await fixture.Jobs.SeedSessionAsync();
        var job = JobTestContext.CreateJob(session, segments);
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        await fixture.Jobs.Mongo.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == fixture.WorkerId,
            Builders<TranscodeWorker>.Update.Set(value => value.Configuration.MaxCacheBytes, 1024 * 1024));
        Assert.Empty(await fixture.Scheduler.GetRemoteEligibleJobIdsAsync(CancellationToken.None));
        Assert.Empty((await fixture.HeartbeatAsync()).ClipAssignments);
        await fixture.Jobs.Mongo.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == fixture.WorkerId,
            Builders<TranscodeWorker>.Update.Set(value => value.Configuration.MaxCacheBytes, 1024L * 1024 * 1024));
        fixture.Ranges.HeaderBytes = 1024 * 1024;
        fixture.Preparation.Invalidate(job);
        Assert.Empty(await fixture.Scheduler.GetRemoteEligibleJobIdsAsync(CancellationToken.None));
        Assert.Empty((await fixture.HeartbeatAsync()).ClipAssignments);
        var queued = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync();
        Assert.Equal("Queued", queued.State);
        Assert.Equal(0, queued.Attempt);
    }

    /// <summary>Slow input preparation runs outside heartbeats, so another active clip keeps renewing its lease.</summary>
    [Fact]
    public async Task SlowPreparationDoesNotBlockActiveJobRenewal()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (active, lease) = await fixture.AssignAsync();
        var pending = new MediaJob
        {
            OwnerId = active.OwnerId, SessionId = active.SessionId, Title = "second",
            SourceSegments = active.SourceSegments, SegmentIds = active.SegmentIds,
            SourceRevision = active.SourceRevision, StartMs = active.StartMs, EndMs = active.EndMs
        };
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(pending);
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == active.Id,
            Builders<MediaJob>.Update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(5)));
        fixture.Ranges.BlockPlans = true;
        try
        {
            var heartbeat = new Yohuke.LiveRecorder.Transcoding.Contracts.WorkerHeartbeat
            {
                AppliedConfigurationRevision = fixture.Revision, Telemetry = new() { FreeBytes = long.MaxValue },
                Jobs = [new() { JobId = active.Id, Attempt = lease.Attempt, LeaseToken = lease.LeaseToken }]
            };
            var state = await fixture.Scheduler.HeartbeatAsync(fixture.WorkerId, heartbeat, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(Assert.Single(state.Leases).Accepted);
            Assert.Empty(state.ClipAssignments);
            await fixture.Ranges.PlanEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = await fixture.Scheduler.HeartbeatAsync(fixture.WorkerId, heartbeat, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(Assert.Single(second.Leases).Accepted);
            Assert.Empty(second.ClipAssignments);
            var renewed = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == active.Id).SingleAsync();
            Assert.True(renewed.LeaseUntilUtc > DateTime.UtcNow.AddSeconds(60));
            fixture.Ranges.ContinuePlan.TrySetResult();
            await fixture.Preparation.WaitForReadyAsync(pending).WaitAsync(TimeSpan.FromSeconds(5));
            var ready = await fixture.Scheduler.HeartbeatAsync(fixture.WorkerId, heartbeat, CancellationToken.None);
            Assert.Equal(pending.Id, Assert.Single(ready.ClipAssignments).JobId);
        }
        finally
        {
            fixture.Ranges.ContinuePlan.TrySetResult();
        }
    }

    /// <summary>A heartbeat remains bound to its connection credential generation even after a fresh Worker lookup.</summary>
    [Fact]
    public async Task RotatedConnectionCannotRenewOrClaimUsingFreshWorkerSnapshot()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var oldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Secret))).ToLowerInvariant();
        await fixture.Workers.RotateAsync(fixture.WorkerId, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Scheduler.HeartbeatAsync(fixture.WorkerId,
            new() { AppliedConfigurationRevision = fixture.Revision, Telemetry = new() { FreeBytes = long.MaxValue } },
            CancellationToken.None, oldHash));
        Assert.Equal(401, error.StatusCode);
    }

    /// <summary>Appending to a recording preserves the planned committed range; truncation invalidates it.</summary>
    [Fact]
    public async Task GrowingRecordingUsesFixedCommittedSnapshot()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync(true);
        var span = Assert.Single(assignment.Inputs);
        var snapshotLength = job.SourceSegments[0].CommittedBytes;
        Assert.Equal(snapshotLength, fixture.Ranges.LastCommittedBytes);
        Assert.Equal(snapshotLength, span.Range!.SourceLength);
        var input = await fixture.Transfers.GetInputAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, span.Id);
        await File.AppendAllTextAsync(input.Path, "appended-recording-bytes");
        var after = await fixture.Transfers.GetInputAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, span.Id);
        Assert.Equal(input.SourceLength, after.SourceLength);
        await using (var output = new FileStream(input.Path, FileMode.Open, FileAccess.Write))
        {
            output.SetLength(snapshotLength - 1);
        }
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.GetInputAsync(
            fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, span.Id));
        Assert.Equal(410, error.StatusCode);
    }

    /// <summary>Reclaimed attempts and rotated credentials cannot use an earlier execution token.</summary>
    [Fact]
    public async Task ReclaimAndCredentialRotationFencePreviousAttempts()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, old) = await fixture.AssignAsync();
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<MediaJob>.Update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        var next = Assert.Single((await fixture.HeartbeatAsync()).ClipAssignments);
        Assert.True(next.Attempt > old.Attempt);
        Assert.NotEqual(next.LeaseToken, old.LeaseToken);
        var expired = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.GetUploadOffsetAsync(
            fixture.WorkerId, job.Id, old.Attempt, old.LeaseToken, 100));
        Assert.Equal(409, expired.StatusCode);
        await fixture.Workers.RotateAsync(fixture.WorkerId, CancellationToken.None);
        var revoked = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.GetUploadOffsetAsync(
            fixture.WorkerId, job.Id, next.Attempt, next.LeaseToken, 100));
        Assert.Equal(409, revoked.StatusCode);
        Assert.Equal("Queued", (await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync()).State);
    }

    /// <summary>Failed chunks retain their previous committed offset and do not permit oversized input.</summary>
    [Fact]
    public async Task UploadRollbackPreservesCommittedOffset()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        await using var first = new MemoryStream(new byte[] { 1, 2, 3 });
        Assert.Equal(3, await fixture.Transfers.UploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 0, 5, first));
        await using var invalid = new MemoryStream(new byte[] { 4, 5, 6 });
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.UploadAsync(
            fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 3, 5, invalid));
        Assert.Equal(413, error.StatusCode);
        Assert.Equal(3, await fixture.Transfers.GetUploadOffsetAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 5));
    }

    /// <summary>Only verified content can become a clip and a lost completion response can be confirmed idempotently.</summary>
    [Fact]
    public async Task VerifiedOutputPublishesThroughExistingClipAndCompletionIsIdempotent()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var bytes = "verified-clip-output"u8.ToArray();
        await using var input = new MemoryStream(bytes);
        await fixture.Transfers.UploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 0, bytes.Length, input);
        var manifest = new ClipOutputManifest
        {
            Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ActualStartMs = 0, ActualEndMs = 1000
        };
        var invalid = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.CompleteAsync(
            fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, manifest with { Sha256 = new('0', 64) }));
        Assert.Equal(422, invalid.StatusCode);
        await fixture.Transfers.CompleteAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, manifest);
        await fixture.Transfers.CompleteAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, manifest);
        var current = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync();
        var clip = await fixture.Jobs.Mongo.Collection<Clip>("clips").Find(value => value.JobId == job.Id).SingleAsync();
        Assert.Equal("Completed", current.State);
        Assert.Equal(100, current.Progress);
        Assert.Null(current.LeaseOwner);
        Assert.Equal(current.OutputRelativePath, clip.RelativePath);
        Assert.Equal(assignment.Attempt, clip.PublicationAttempt);
        Assert.False(Directory.Exists(Path.Combine(fixture.Jobs.Root, ".work", "jobs", job.Id, assignment.Attempt.ToString())));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.Jobs.Root, clip.RelativePath)));
        Assert.Equal(0, fixture.Jobs.Processor.ExportCalls);
    }

    /// <summary>Cancellation prevents further input access or MP4 upload even before the remote node observes its heartbeat.</summary>
    [Fact]
    public async Task CancellationImmediatelyRevokesRemoteTransfer()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<MediaJob>.Update.Set(value => value.CancelRequested, true));
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.GetUploadOffsetAsync(
            fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 100));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(0, await fixture.Jobs.Mongo.Collection<Clip>("clips").CountDocumentsAsync(_ => true));
    }

    /// <summary>Revocation can finish while output verification is slow and prevents the old completion from publishing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowOutputVerificationDoesNotDelayCancellationOrCredentialRotation(bool rotate)
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var bytes = "verified-clip-output"u8.ToArray();
        await using var input = new MemoryStream(bytes);
        await fixture.Transfers.UploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 0, bytes.Length, input);
        var manifest = new ClipOutputManifest
        {
            Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ActualStartMs = 0, ActualEndMs = 1000
        };
        fixture.Jobs.Processor.BlockProbe = true;
        try
        {
            var completion = fixture.Transfers.CompleteAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, manifest);
            await fixture.Jobs.Processor.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (rotate)
            {
                await fixture.Workers.RotateAsync(fixture.WorkerId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            }
            else
            {
                var jobs = new MediaJobService(fixture.Jobs.Mongo, new LibraryService(fixture.Jobs.Mongo, TimeProvider.System),
                    fixture.Jobs.Publisher, fixture.Jobs.Paths);
                await jobs.CancelAsync(job).WaitAsync(TimeSpan.FromSeconds(2));
            }
            fixture.Jobs.Processor.ContinueProbe.TrySetResult();
            var error = await Assert.ThrowsAsync<InfrastructureException>(() => completion);
            Assert.Equal(409, error.StatusCode);
            Assert.Equal(0, await fixture.Jobs.Mongo.Collection<Clip>("clips").CountDocumentsAsync(value => !value.IsTrashed));
        }
        finally
        {
            fixture.Jobs.Processor.ContinueProbe.TrySetResult();
        }
    }

    /// <summary>In-place changes to a growing file cannot publish output under its earlier range content identity.</summary>
    [Fact]
    public async Task InPlaceCommittedContentChangeIsRejectedBeforePublication()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync(true);
        var span = assignment.Inputs.Single();
        var source = await fixture.Transfers.GetInputAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, span.Id);
        var sourceBytes = await File.ReadAllBytesAsync(source.Path);
        sourceBytes[0] ^= 0xff;
        await using (var changed = new FileStream(source.Path, FileMode.Open, FileAccess.Write))
        {
            await changed.WriteAsync(sourceBytes);
        }
        var bytes = "verified-clip-output"u8.ToArray();
        await using var input = new MemoryStream(bytes);
        await fixture.Transfers.UploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 0, bytes.Length, input);
        var manifest = new ClipOutputManifest
        {
            Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ActualStartMs = 0, ActualEndMs = 1000
        };
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.CompleteAsync(
            fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, manifest));
        Assert.Equal(410, error.StatusCode);
        Assert.Equal(0, await fixture.Jobs.Mongo.Collection<Clip>("clips").CountDocumentsAsync(value => !value.IsTrashed));
    }

    /// <summary>Cancellation cannot interleave between final lease verification and the completion commit.</summary>
    [Fact]
    public async Task CancellationWaitsForAtomicPublicationBoundary()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var current = await fixture.Leases.RequireAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, CancellationToken.None);
        var session = await fixture.Jobs.Mongo.Collection<BroadcastSession>("sessions").Find(value => value.Id == job.SessionId).SingleAsync();
        var relative = Path.Combine(session.DirectoryPath, "切片", job.Id, "publication.mp4");
        var path = Path.Combine(fixture.Jobs.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "verified-output");
        var manifest = JobOutputManifest.Create(current);
        manifest.Paths.Add(relative);
        manifest.ArtifactLengths.Add(relative, new FileInfo(path).Length);
        var publication = new ClipPublicationService(fixture.Jobs.Mongo, fixture.Jobs.Paths);
        var jobs = new MediaJobService(fixture.Jobs.Mongo, new LibraryService(fixture.Jobs.Mongo, TimeProvider.System),
            fixture.Jobs.Publisher, fixture.Jobs.Paths);
        Task? cancellation = null;
        var checks = 0;
        await using (await MediaJobExecutionLock.AcquireAsync(fixture.Jobs.Paths, job.Id))
        {
            await publication.PublishAsync(current, session, manifest, fixture.Jobs.Settings.Settings, async ct =>
            {
                var owned = await fixture.Leases.RequireAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, ct);
                if (++checks == 2)
                {
                    cancellation = jobs.CancelAsync(current);
                    Assert.False(cancellation.IsCompleted);
                }
                return owned;
            }, CancellationToken.None);
            var pending = await fixture.Jobs.Mongo.Collection<Clip>("clips").Find(value => value.JobId == job.Id).SingleAsync();
            Assert.True(pending.IsTrashed);
            Assert.True(pending.PublicationPending);
            var committed = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(
                ClipLeaseService.Fence(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken),
                Builders<MediaJob>.Update.Set(value => value.State, "Completed").Set(value => value.OutputRelativePath, relative)
                    .Set(value => value.LeaseOwner, null).Set(value => value.LeaseUntilUtc, null));
            Assert.Equal(1, committed.MatchedCount);
            await publication.ActivateAsync(current, CancellationToken.None);
            Assert.False(cancellation!.IsCompleted);
        }
        await cancellation!;
        var completed = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync();
        Assert.Equal("Completed", completed.State);
        Assert.False(completed.CancelRequested);
        var clip = await fixture.Jobs.Mongo.Collection<Clip>("clips").Find(value => value.JobId == job.Id).SingleAsync();
        Assert.False(clip.IsTrashed);
        Assert.False(clip.PublicationPending);
    }

    /// <summary>Uncommitted clips remain hidden and completion recovery never restores a clip deleted by its owner.</summary>
    [Fact]
    public async Task PendingClipRequiresCompletionAndCannotReviveUserTrash()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var current = await fixture.Leases.RequireAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, CancellationToken.None);
        var relative = "pending-output.mp4";
        await File.WriteAllTextAsync(Path.Combine(fixture.Jobs.Root, relative), "verified-output");
        await fixture.Jobs.Mongo.Collection<Clip>("clips").InsertOneAsync(new()
        {
            Id = job.Id, JobId = job.Id, RelativePath = relative, PublicationAttempt = assignment.Attempt,
            IsTrashed = true, PublicationPending = true, Bytes = new FileInfo(Path.Combine(fixture.Jobs.Root, relative)).Length
        });
        var publication = new ClipPublicationService(fixture.Jobs.Mongo, fixture.Jobs.Paths);
        await Assert.ThrowsAsync<InfrastructureException>(() => publication.ActivateAsync(current, CancellationToken.None));
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<MediaJob>.Update.Set(value => value.State, "Completed").Set(value => value.OutputRelativePath, relative));
        await publication.ActivateAsync(current, CancellationToken.None);
        await fixture.Jobs.Mongo.Collection<Clip>("clips").UpdateOneAsync(value => value.Id == job.Id,
            Builders<Clip>.Update.Set(value => value.IsTrashed, true));
        await publication.ActivateAsync(current, CancellationToken.None);
        Assert.True((await fixture.Jobs.Mongo.Collection<Clip>("clips").Find(value => value.Id == job.Id).SingleAsync()).IsTrashed);
    }

    /// <summary>A committed pending output remains referenced while its original session is permanently removed.</summary>
    [Fact]
    public async Task PendingPublicationSurvivesSourceSessionTrashAndPurge()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var session = await fixture.Jobs.Mongo.Collection<BroadcastSession>("sessions").Find(value => value.Id == job.SessionId).SingleAsync();
        var relative = Path.Combine(session.DirectoryPath, "切片", job.Id, "pending.mp4");
        var file = Path.Combine(fixture.Jobs.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "committed-pending-output");
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<MediaJob>.Update.Set(value => value.State, "Completed").Set(value => value.OutputRelativePath, relative)
                .Set(value => value.LeaseOwner, null).Set(value => value.LeaseUntilUtc, null));
        await fixture.Jobs.Mongo.Collection<Clip>("clips").InsertOneAsync(new()
        {
            Id = job.Id, JobId = job.Id, SessionId = session.Id, RelativePath = relative, PublicationAttempt = assignment.Attempt,
            IsTrashed = true, PublicationPending = true, Bytes = new FileInfo(file).Length
        });
        var trash = new SessionTrashService(fixture.Jobs.Mongo, fixture.Jobs.Settings, fixture.Jobs.Paths);
        await trash.TrashAsync(session.Id, "admin");
        var entry = await fixture.Jobs.Mongo.Collection<TrashEntry>("trash").Find(value => value.EntityId == session.Id).SingleAsync();
        Assert.True(entry.RetainedInPlace);
        await trash.PurgeAsync(entry);
        Assert.True(File.Exists(file));
        var current = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync();
        await new ClipPublicationService(fixture.Jobs.Mongo, fixture.Jobs.Paths).ActivateAsync(current, CancellationToken.None);
        Assert.False((await fixture.Jobs.Mongo.Collection<Clip>("clips").Find(value => value.Id == job.Id).SingleAsync()).IsTrashed);
    }
}
