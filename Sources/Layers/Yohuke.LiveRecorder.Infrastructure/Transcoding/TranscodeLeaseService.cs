using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>统一校验节点、执行代次、随机租约及当前媒体版本。</summary>
public sealed class TranscodeLeaseService(IMongoContext context, TranscodeWorkerService workers, TranscodeAssetRegistry assets)
{
    /// <summary>恢复尚未被接管的同一执行代次，并以可重发的凭据轮换隔离旧进程。</summary>
    public async Task<WorkerJobRecoveryStatus> RecoverAsync(string workerId, WorkerJobRecoveryRequest request, IAppPaths paths,
        CancellationToken cancellationToken)
    {
        var result = new WorkerJobRecoveryStatus { JobId = request.JobId, Attempt = request.Attempt };
        if (!ValidRecovery(request))
        {
            return result;
        }
        var gate = WorkerTransferLocks.Gate("Transcoding", request.JobId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + request.JobId, cancellationToken);
            await workers.RequireAsync(workerId, cancellationToken);
            var jobs = context.Collection<TranscodeJob>("transcodeJobs");
            var hashes = new[] { TranscodeWorkerService.SecretHash(request.LeaseToken), TranscodeWorkerService.SecretHash(request.NextLeaseToken) };
            var filter = Builders<TranscodeJob>.Filter.Where(job => job.Id == request.JobId && job.WorkerId == workerId
                && job.Attempt == request.Attempt && !job.CancelRequested
                && (job.State == "Running" || job.State == "Publishing" || job.State == "Completed"))
                & Builders<TranscodeJob>.Filter.In(job => job.LeaseTokenHash, hashes);
            var job = await jobs.Find(filter).FirstOrDefaultAsync(cancellationToken);
            if (job is null)
            {
                return result;
            }
            result.State = job.State;
            try
            {
                await assets.RequireActiveAsync(job.AssetId, job.SourceVersion, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                return result;
            }
            if (job.State == "Completed" && job.PublicationReady)
            {
                return result with { Disposition = "Completed" };
            }
            var update = Builders<TranscodeJob>.Update.Set(value => value.LeaseTokenHash, hashes[1]);
            if (job.State != "Completed")
            {
                update = update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(90));
            }
            var changed = await jobs.UpdateOneAsync(filter & Builders<TranscodeJob>.Filter.Eq(value => value.State, job.State),
                update, cancellationToken: cancellationToken);
            return changed.MatchedCount == 0 ? result : result with { Disposition = "Resume" };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>确认已提交完成、但仍在恢复发布的执行身份。</summary>
    public Task<bool> IsFinalizingAsync(string workerId, string jobId, int attempt, string leaseToken, CancellationToken cancellationToken) =>
        context.Collection<TranscodeJob>("transcodeJobs").Find(job => job.Id == jobId && job.WorkerId == workerId && job.Attempt == attempt
            && job.LeaseTokenHash == TranscodeWorkerService.SecretHash(leaseToken) && job.State == "Completed" && !job.CancelRequested)
            .AnyAsync(cancellationToken);

    internal static bool ValidRecovery(WorkerJobRecoveryRequest request) => request.JobId is { Length: > 0 and <= 128 }
        && request.JobId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') && request.Attempt > 0
        && request.LeaseToken is { Length: > 0 and <= 256 } && request.NextLeaseToken is { Length: 64 }
        && request.NextLeaseToken.All(char.IsAsciiHexDigit) && request.NextLeaseToken != request.LeaseToken;

    /// <summary>在数据传输和最终发布边界重新验证完整执行身份。</summary>
    public async Task<TranscodeJob> RequireAsync(string workerId, string jobId, int attempt, string leaseToken,
        CancellationToken cancellationToken)
    {
        await workers.RequireAsync(workerId, cancellationToken);
        var job = await context.Collection<TranscodeJob>("transcodeJobs")
            .Find(Fence(workerId, jobId, attempt, leaseToken)).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("转码任务租约已失效。", 409);
        try
        {
            await assets.RequireActiveAsync(job.AssetId, job.SourceVersion, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new InfrastructureException("转码源文件已被移除或替换。", 410);
        }
        return job;
    }

    /// <summary>构造只允许当前有效执行者提交的数据库条件。</summary>
    public static FilterDefinition<TranscodeJob> Fence(string workerId, string jobId, int attempt, string leaseToken) =>
        Builders<TranscodeJob>.Filter.Where(x => x.Id == jobId && x.WorkerId == workerId && x.Attempt == attempt
            && x.LeaseTokenHash == TranscodeWorkerService.SecretHash(leaseToken) && !x.CancelRequested
            && (x.State == "Running" || x.State == "Publishing"))
        & new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$LeaseUntilUtc", "$$NOW" }));
}
