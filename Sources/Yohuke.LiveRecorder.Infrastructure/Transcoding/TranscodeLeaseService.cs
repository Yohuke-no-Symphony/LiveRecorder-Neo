using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>统一校验节点、执行代次、随机租约及当前媒体版本。</summary>
public sealed class TranscodeLeaseService(IMongoContext context, TranscodeWorkerService workers, TranscodeAssetRegistry assets)
{
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
