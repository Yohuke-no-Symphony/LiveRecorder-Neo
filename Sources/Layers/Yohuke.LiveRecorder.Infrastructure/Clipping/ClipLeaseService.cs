using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Validates the Worker kind, clipping attempt, random lease and immutable source version.</summary>
public sealed class ClipLeaseService(IMongoContext context, TranscodeWorkerService workers, ClipInputPlanner inputs)
{
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
