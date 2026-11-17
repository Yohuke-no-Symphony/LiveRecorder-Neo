using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证有限 Worker 恢复的身份围栏、上传断点及重试亲和性。</summary>
public sealed class WorkerRecoveryTests
{
    /// <summary>应答丢失后重发认领保持同代次与上传偏移，旧进程的凭据立即失效。</summary>
    [Fact]
    public async Task ClippingRecoveryIsIdempotentAndPreservesTheUploadedPrefix()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var stages = WorkerStageSnapshotPolicy.Create("Clipping");
        foreach (var stage in stages.Take(4))
        {
            stage.State = "Completed";
            stage.Progress = 100;
        }
        stages[4].State = "Running";
        stages[4].ElapsedMs = 1250;
        stages[4].Activity = new() { BytesTransferred = 4, TotalBytes = 20 };
        var heartbeat = await fixture.Scheduler.HeartbeatAsync(fixture.WorkerId, new()
        {
            AppliedConfigurationRevision = fixture.Revision, Telemetry = new() { FreeBytes = long.MaxValue },
            Jobs = [new() { JobId = job.Id, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken, Stage = "Uploading", Stages = stages }]
        }, CancellationToken.None);
        Assert.True(Assert.Single(heartbeat.Leases).Accepted);
        await fixture.Transfers.BeginUploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken,
            new() { ManifestFingerprint = new('a', 64) });
        await fixture.Transfers.UploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken,
            0, 20, new MemoryStream([1, 2, 3, 4]));
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<MediaJob>.Update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        var request = new WorkerJobRecoveryRequest
        {
            JobId = job.Id, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken, NextLeaseToken = new('b', 64)
        };
        var first = await fixture.Leases.RecoverAsync(fixture.WorkerId, request, fixture.Jobs.Paths, CancellationToken.None);
        var repeated = await fixture.Leases.RecoverAsync(fixture.WorkerId, request, fixture.Jobs.Paths, CancellationToken.None);
        Assert.Equal("Resume", first.Disposition);
        Assert.Equal("Resume", repeated.Disposition);
        Assert.Equal(assignment.Attempt, first.Attempt);
        var stale = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Transfers.GetUploadOffsetAsync(
            fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 20));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal(4, await fixture.Transfers.GetUploadOffsetAsync(fixture.WorkerId, job.Id, assignment.Attempt, request.NextLeaseToken, 20));
        var saved = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync();
        Assert.Equal(assignment.Attempt, saved.Attempt);
        Assert.True(saved.LeaseUntilUtc > DateTime.UtcNow.AddSeconds(60));
        Assert.Equal(6, saved.Stages.Count);
        Assert.Equal("Completed", saved.Stages[0].State);
        Assert.Equal(1250, saved.Stages[4].ElapsedMs);
        Assert.Equal(4, saved.Stages[4].Activity!.BytesTransferred);
    }

    /// <summary>不同成品会话只清空当前代次的旧字节，相同成品仍能续传。</summary>
    [Fact]
    public async Task RebuiltClipResetsOnlyItsOwnUnpublishedUpload()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var retained = Path.Combine(fixture.Jobs.Root, ".work", "jobs", job.Id, "999", "keep.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        await File.WriteAllTextAsync(retained, "other-attempt");
        var original = new WorkerUploadSession { ManifestFingerprint = new('a', 64) };
        await fixture.Transfers.BeginUploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, original);
        await fixture.Transfers.UploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken,
            0, 20, new MemoryStream([1, 2, 3, 4]));
        await fixture.Transfers.BeginUploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, original);
        Assert.Equal(4, await fixture.Transfers.GetUploadOffsetAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 20));
        await fixture.Transfers.BeginUploadAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken,
            new() { ManifestFingerprint = new('c', 64) });
        Assert.Equal(0, await fixture.Transfers.GetUploadOffsetAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken, 30));
        Assert.True(File.Exists(retained));
    }

    /// <summary>取消和已被接管的任务不能凭持久旧凭据复活。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryRejectsCancelledAndReassignedExecutions(bool cancelled)
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var update = cancelled
            ? Builders<MediaJob>.Update.Set(value => value.State, "Cancelled").Set(value => value.CancelRequested, true)
            : Builders<MediaJob>.Update.Inc(value => value.Attempt, 1).Set(value => value.RemoteWorkerId, "replacement");
        await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id, update);
        var result = await fixture.Leases.RecoverAsync(fixture.WorkerId, new()
        {
            JobId = job.Id, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken, NextLeaseToken = new('c', 64)
        }, fixture.Jobs.Paths, CancellationToken.None);
        Assert.Equal("Rejected", result.Disposition);
    }

    /// <summary>失败提交保留有序步骤诊断，重试后原节点在线且有容量时优先再次认领。</summary>
    [Fact]
    public async Task ClippingRetryRetainsStagesAndPrefersTheOriginalWorker()
    {
        await using var fixture = await ClippingTestContext.CreateAsync();
        var (job, assignment) = await fixture.AssignAsync();
        var stages = WorkerStageSnapshotPolicy.Create("Clipping");
        stages[0].State = "Completed";
        stages[2].State = "Failed";
        await fixture.Transfers.FinishAsync(fixture.WorkerId, job.Id, assignment.Attempt, assignment.LeaseToken,
            new() { State = "Failed", ErrorCode = "EncodingFailed", Stages = stages });
        var service = new MediaJobService(fixture.Jobs.Mongo, new LibraryService(fixture.Jobs.Mongo, TimeProvider.System), fixture.Jobs.Publisher, fixture.Jobs.Paths);
        await service.RetryAsync(job);
        var queued = await fixture.Jobs.Mongo.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).SingleAsync();
        Assert.Equal(fixture.WorkerId, queued.ResumeWorkerId);
        Assert.Equal("Completed", queued.Stages[0].State);
        var other = new TranscodeWorker
        {
            Kind = "Clipping", Name = "other", Configuration = new() { TempDirectory = fixture.Jobs.DirectoryPath, MinimumFreeBytes = 0 },
            Capabilities = new() { ClippingVersion = 1 }
        };
        await fixture.Jobs.Mongo.Collection<TranscodeWorker>("transcodeWorkers").InsertOneAsync(other);
        Assert.Empty((await fixture.Scheduler.HeartbeatAsync(other.Id,
            new() { AppliedConfigurationRevision = 1, Telemetry = new() { FreeBytes = long.MaxValue } }, CancellationToken.None)).ClipAssignments);
        await fixture.Preparation.WaitForReadyAsync(queued).WaitAsync(TimeSpan.FromSeconds(5));
        var next = Assert.Single((await fixture.HeartbeatAsync()).ClipAssignments);
        Assert.Equal(assignment.Attempt + 1, next.Attempt);
        Assert.Equal(assignment.CheckpointGeneration, next.CheckpointGeneration);
    }

    /// <summary>转码过期恢复保持代次，普通重试复用检查点而强制重建创建新检查点代次。</summary>
    [Fact]
    public async Task TranscodeRecoveryAndForceKeepExecutionAndCheckpointGenerationsSeparate()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var assets = new TranscodeAssetRegistry(fixture.Mongo, fixture.Paths);
        var workers = new TranscodeWorkerService(fixture.Mongo, fixture.Paths);
        var leases = new TranscodeLeaseService(fixture.Mongo, workers, assets);
        var scheduler = new TranscodeJobScheduler(fixture.Mongo, fixture.Settings, fixture.Paths,
            new LibraryService(fixture.Mongo, TimeProvider.System), assets, workers, leases);
        var worker = new TranscodeWorker
        {
            Name = "recovery", Configuration = new() { TempDirectory = fixture.DirectoryPath, MinimumFreeBytes = 0 },
            Capabilities = new() { HlsPackagingVersion = 1, Encoders = [new() { Encoder = "h264_nvenc", Available = true }] }
        };
        await fixture.Mongo.Collection<TranscodeWorker>("transcodeWorkers").InsertOneAsync(worker);
        await scheduler.QueueAsync([session.Id], null, false, CancellationToken.None);
        var assignment = Assert.Single((await scheduler.HeartbeatAsync(worker.Id,
            new() { AppliedConfigurationRevision = 1, Telemetry = new() { FreeBytes = long.MaxValue } }, CancellationToken.None)).Assignments);
        var jobs = fixture.Mongo.Collection<TranscodeJob>("transcodeJobs");
        await jobs.UpdateOneAsync(value => value.Id == assignment.JobId,
            Builders<TranscodeJob>.Update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        var request = new WorkerJobRecoveryRequest
        {
            JobId = assignment.JobId, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken, NextLeaseToken = new('d', 64)
        };
        Assert.Equal("Resume", (await leases.RecoverAsync(worker.Id, request, fixture.Paths, CancellationToken.None)).Disposition);
        await Assert.ThrowsAsync<InfrastructureException>(() => leases.RequireAsync(worker.Id, assignment.JobId, assignment.Attempt, assignment.LeaseToken, CancellationToken.None));
        Assert.Equal(assignment.Attempt, (await leases.RequireAsync(worker.Id, assignment.JobId, assignment.Attempt, request.NextLeaseToken, CancellationToken.None)).Attempt);
        await jobs.UpdateOneAsync(value => value.Id == assignment.JobId,
            Builders<TranscodeJob>.Update.Set(value => value.State, "Failed").Set(value => value.LeaseTokenHash, null));
        await scheduler.RetryAsync(assignment.JobId, CancellationToken.None);
        var retried = await jobs.Find(value => value.Id == assignment.JobId).SingleAsync();
        Assert.Equal(assignment.CheckpointGeneration, retried.CheckpointGeneration);
        Assert.Equal(worker.Id, retried.ResumeWorkerId);
        await jobs.UpdateOneAsync(value => value.Id == assignment.JobId, Builders<TranscodeJob>.Update.Set(value => value.State, "Completed"));
        Assert.Equal(1, await scheduler.QueueAsync([session.Id], null, true, CancellationToken.None));
        var forced = await jobs.Find(value => value.Id == assignment.JobId).SingleAsync();
        Assert.Equal(assignment.CheckpointGeneration + 1, forced.CheckpointGeneration);
        Assert.Null(forced.ResumeWorkerId);
    }
}
