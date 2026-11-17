using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Validates the Worker kind, clipping attempt, random lease and immutable source version.</summary>
public sealed class ClipLeaseService(IMongoContext context, TranscodeWorkerService workers, ClipInputPlanner inputs)
{
    /// <summary>以同一执行代次恢复断点，并通过幂等凭据轮换撤销旧进程写入权。</summary>
    public async Task<WorkerJobRecoveryStatus> RecoverAsync(string workerId, WorkerJobRecoveryRequest request, IAppPaths paths,
        CancellationToken cancellationToken)
    {
        var result = new WorkerJobRecoveryStatus { JobId = request.JobId, Attempt = request.Attempt };
        if (!TranscodeLeaseService.ValidRecovery(request))
        {
            return result;
        }
        var gate = WorkerTransferLocks.Gate("Clipping", request.JobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, request.JobId, cancellationToken);
            await RequireWorkerAsync(workerId, cancellationToken);
            var jobs = context.Collection<MediaJob>("jobs");
            var hashes = new[] { TranscodeWorkerService.SecretHash(request.LeaseToken), TranscodeWorkerService.SecretHash(request.NextLeaseToken) };
            var filter = Builders<MediaJob>.Filter.Where(job => job.Id == request.JobId && job.Kind == "Clip"
                && job.RemoteWorkerId == workerId && job.Attempt == request.Attempt && !job.CancelRequested
                && (job.State == "Running" || job.State == "Publishing" || job.State == "Completed"))
                & Builders<MediaJob>.Filter.In(job => job.RemoteLeaseTokenHash, hashes);
            var job = await jobs.Find(filter).FirstOrDefaultAsync(cancellationToken);
            if (job is null)
            {
                return result;
            }
            result.State = job.State;
            try
            {
                await inputs.ValidateSourcesAsync(job, cancellationToken);
            }
            catch (InfrastructureException error) when (error.StatusCode is 404 or 409 or 410)
            {
                return result;
            }
            if (job.State == "Completed" && await context.Collection<Clip>("clips")
                    .Find(clip => clip.JobId == job.Id && clip.PublicationAttempt == job.Attempt && !clip.PublicationPending).AnyAsync(cancellationToken))
            {
                return result with { Disposition = "Completed" };
            }
            var update = Builders<MediaJob>.Update.Set(value => value.RemoteLeaseTokenHash, hashes[1]);
            if (job.State != "Completed")
            {
                update = update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(90));
            }
            var changed = await jobs.UpdateOneAsync(filter & Builders<MediaJob>.Filter.Eq(value => value.State, job.State),
                update, cancellationToken: cancellationToken);
            return changed.MatchedCount == 0 ? result : result with { Disposition = "Resume" };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>确认已提交完成的身份，使发布确认重放期间仍可续约本机运行状态。</summary>
    public Task<bool> IsFinalizingAsync(string workerId, string jobId, int attempt, string token, CancellationToken cancellationToken) =>
        context.Collection<MediaJob>("jobs").Find(job => job.Id == jobId && job.Kind == "Clip" && job.RemoteWorkerId == workerId
            && job.Attempt == attempt && job.RemoteLeaseTokenHash == TranscodeWorkerService.SecretHash(token)
            && job.State == "Completed" && !job.CancelRequested).AnyAsync(cancellationToken);

    /// <summary>Requires an active clipping lease and unchanged committed source snapshots.</summary>
    public async Task<MediaJob> RequireAsync(string workerId, string jobId, int attempt, string token, CancellationToken cancellationToken)
    {
        await RequireWorkerAsync(workerId, cancellationToken);
        var job = await context.Collection<MediaJob>("jobs").Find(Fence(workerId, jobId, attempt, token))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("切片任务租约已失效。", 409);
        await inputs.ValidateSourcesAsync(job, cancellationToken);
        return job;
    }

    /// <summary>Requires an enabled node registered for clipping rather than transcoding.</summary>
    public async Task RequireWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        if ((await workers.RequireAsync(workerId, cancellationToken)).Kind != "Clipping")
        {
            throw new InfrastructureException("此节点不接受切片任务。", 409);
        }
    }

    /// <summary>Builds a database fence that excludes cancellation, old attempts and expired executions.</summary>
    public static FilterDefinition<MediaJob> Fence(string workerId, string jobId, int attempt, string token) =>
        Builders<MediaJob>.Filter.Where(value => value.Id == jobId && value.Kind == "Clip"
            && value.RemoteWorkerId == workerId && value.LeaseOwner == workerId && value.Attempt == attempt
            && value.RemoteLeaseTokenHash == TranscodeWorkerService.SecretHash(token) && !value.CancelRequested
            && (value.State == "Running" || value.State == "Publishing"))
        & new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$LeaseUntilUtc", "$$NOW" }));
}
