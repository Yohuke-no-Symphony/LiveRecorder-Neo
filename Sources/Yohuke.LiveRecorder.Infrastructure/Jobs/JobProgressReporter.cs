using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Jobs;

/// <summary>合并单调媒体进度，完成或失去租约后到达的回调不能改写作业。</summary>
public sealed class JobProgressReporter(IMongoContext context, IStatusPublisher publisher, MediaJob job, string workerId, double rangeStart = 0, double rangeEnd = 1) : IProgress<double>
{
    private readonly object gate = new();
    private double current;
    private DateTime lastPublished;
    private Task pending = Task.CompletedTask;

    /// <inheritdoc />
    public void Report(double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }
        lock (gate)
        {
            var fraction = Math.Clamp(value <= 1 ? value : value / 100, 0, 1);
            current = Math.Max(current, Math.Clamp((rangeStart + (rangeEnd - rangeStart) * fraction) * 100, 0, 99.9));
            if (DateTime.UtcNow - lastPublished >= TimeSpan.FromSeconds(1) && pending.IsCompleted)
            {
                lastPublished = DateTime.UtcNow;
                pending = WriteAsync(current, CancellationToken.None);
            }
        }
    }

    /// <summary>等待已发出的更新并提交最新进度；所有更新都在数据库中执行租约和状态校验。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task previous;
        lock (gate)
        {
            previous = pending;
        }
        await previous.WaitAsync(cancellationToken);
        double value;
        lock (gate)
        {
            value = current;
        }
        await WriteAsync(value, cancellationToken);
    }

    private async Task WriteAsync(double value, CancellationToken cancellationToken)
    {
        var filter = Builders<MediaJob>.Filter.Where(x => x.Id == job.Id && x.LeaseOwner == workerId && x.Attempt == job.Attempt && x.State == "Running" && !x.CancelRequested)
                     & new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$LeaseUntilUtc", "$$NOW" }));
        var updated = await context.Collection<MediaJob>("jobs").UpdateOneAsync(filter,
            Builders<MediaJob>.Update.Max(x => x.Progress, value), cancellationToken: cancellationToken);
        if (updated.MatchedCount > 0)
        {
            await publisher.PublishAsync("job", job.Id, job.OwnerId, cancellationToken);
        }
    }
}
