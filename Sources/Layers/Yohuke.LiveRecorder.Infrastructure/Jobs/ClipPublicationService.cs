using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Jobs;

/// <summary>Publishes local and remote clips through the same lease-fenced, versioned library operation.</summary>
public sealed class ClipPublicationService(IMongoContext context, IAppPaths paths)
{
    /// <summary>Activates a validated clip only while the caller retains the current execution lease.</summary>
    public async Task PublishAsync(MediaJob job, BroadcastSession session, JobOutputManifest manifest, ServerSettings settings,
        Func<CancellationToken, Task<MediaJob>> requireLease, CancellationToken cancellationToken)
    {
        await using var publicationLock = await MediaJobFiles.AcquirePublicationLockAsync(
            MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath), cancellationToken);
        await requireLease(cancellationToken);
        var clips = context.Collection<Clip>("clips");
        try
        {
            await clips.InsertOneAsync(new() { Id = job.Id, IsTrashed = true }, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }
        var lease = await requireLease(cancellationToken);
        var clip = new Clip
        {
            Id = job.Id, OwnerId = job.OwnerId, SessionId = job.SessionId, JobId = job.Id, Title = job.Title,
            RelativePath = manifest.Paths.Single(),
            Bytes = new FileInfo(MediaPaths.Resolve(paths.RecordsDirectory, manifest.Paths.Single())).Length,
            StartMs = manifest.ActualStartMs, EndMs = manifest.ActualEndMs,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(settings.ClipRetentionDays), PublicationAttempt = job.Attempt,
            IsTrashed = true, PublicationPending = true
        };
        var filter = Builders<Clip>.Filter.Where(value => value.Id == job.Id && value.PublicationAttempt <= job.Attempt)
            & new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { new BsonDateTime(lease.LeaseUntilUtc!.Value), "$$NOW" }));
        if ((await clips.ReplaceOneAsync(filter, clip, cancellationToken: cancellationToken)).MatchedCount == 0)
        {
            throw new OperationCanceledException("切片发布执行权已失效。", cancellationToken);
        }
    }

    /// <summary>Activates an explicitly pending clip only after the corresponding job completion committed.</summary>
    public async Task ActivateAsync(MediaJob job, CancellationToken cancellationToken)
    {
        var completed = await context.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id && value.Attempt == job.Attempt
            && value.Kind == "Clip" && value.State == "Completed" && !value.CancelRequested).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("切片任务尚未提交完成。", 409);
        var filter = Builders<Clip>.Filter.Where(value => value.Id == job.Id && value.PublicationAttempt == job.Attempt
            && value.RelativePath == completed.OutputRelativePath && value.PublicationPending);
        var pending = await context.Collection<Clip>("clips").Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (pending is null)
        {
            return;
        }
        var file = new FileInfo(MediaPaths.Resolve(paths.RecordsDirectory, pending.RelativePath));
        if (!file.Exists || file.Length <= 0 || file.Length != pending.Bytes)
        {
            throw new InfrastructureException("待发布切片文件已丢失或发生变化。", 410);
        }
        await context.Collection<Clip>("clips").UpdateOneAsync(filter,
            Builders<Clip>.Update.Set(value => value.IsTrashed, false).Set(value => value.PublicationPending, false),
            cancellationToken: cancellationToken);
    }
}
