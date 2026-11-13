using Yohuke.LiveRecorder.Application.Localization;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Jobs;

/// <summary>校验任务输入并以持久快照入队。</summary>
public sealed class MediaJobService(IMongoContext context, LibraryService library, IStatusPublisher publisher)
{
    /// <summary>创建不会随浏览器断开而取消的作业。</summary>
    public async Task<MediaJob> CreateAsync(CreateMediaJobRequest request, string ownerId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        if (request.Kind is not ("Clip" or "Repair" or "Merge") || request.Mode is not ("Exact" or "Fast"))
        {
            throw new InfrastructureException(MessageCode.JOB_KIND_INVALID);
        }
        if (request.Kind != "Clip" && !isAdmin)
        {
            throw new InfrastructureException(MessageCode.JOB_ADMIN_REQUIRED, 403);
        }
        await EnsureMutationIndexAsync(cancellationToken);
        var detail = await library.GetAsync(request.SessionId, cancellationToken);
        if (request.Kind == "Clip" && detail.Session.EndedAtUtc is null && detail.Segments.Any(x => !x.IsClosed) &&
            request.EndMs > detail.Segments.Max(x => x.StartOffsetMs + x.DurationMs))
        {
            throw new InfrastructureException(MessageCode.JOB_ARCHIVE_NOT_READY, 425);
        }
        var selected = request.SegmentIds.Count == 0 ? detail.Segments : detail.Segments.Where(x => request.SegmentIds.Contains(x.Id)).ToList();
        if (selected.Count == 0 || (request.SegmentIds.Count > 0 && selected.Count != request.SegmentIds.Distinct().Count()))
        {
            throw new InfrastructureException(MessageCode.JOB_SEGMENT_NOT_FOUND);
        }
        if (request.Kind != "Clip" && (detail.Session.EndedAtUtc is null || selected.Any(x => !x.IsClosed)))
        {
            throw new InfrastructureException(MessageCode.JOB_RECORDING_MUST_FINISH, 409);
        }
        if (request.Kind != "Clip")
        {
            request.StartMs = selected.Min(x => x.StartOffsetMs);
            request.EndMs = selected.Max(x => x.StartOffsetMs + x.DurationMs);
        }
        if (request.EndMs > detail.DurationMs + 1)
        {
            throw new InfrastructureException(MessageCode.JOB_CLIP_END_BEYOND_ARCHIVE);
        }
        var spans = SessionTimeline.ExportSpans(selected, request.StartMs, request.EndMs, x => x);
        if (request.Mode == "Fast" && spans.Any(x => x.IsGap))
        {
            throw new InfrastructureException(MessageCode.JOB_GAP_REQUIRES_EXACT_MODE, 422);
        }
        var queued = await context.Collection<MediaJob>("jobs").CountDocumentsAsync(x => x.OwnerId == ownerId && (x.State == "Queued" || x.State == "Running" || x.State == "Publishing"), cancellationToken: cancellationToken);
        if (!isAdmin && queued >= 5)
        {
            throw new InfrastructureException(MessageCode.JOB_WAIT_FOR_EXISTING, 429);
        }
        var job = new MediaJob
        {
            OwnerId = ownerId,
            SessionId = request.SessionId,
            Kind = request.Kind,
            Title = string.IsNullOrWhiteSpace(request.Title) ? detail.Session.Title : request.Title.Trim(),
            Mode = request.Mode,
            StartMs = request.StartMs,
            EndMs = request.EndMs,
            SegmentIds = selected.Select(x => x.Id).ToList(),
            SourceSegments = selected,
            SourceRevision = detail.Session.MediaRevision,
            MutationKey = request.Kind == "Repair" ? detail.Session.Id : null
        };
        try
        {
            await context.Collection<MediaJob>("jobs").InsertOneAsync(job, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InfrastructureException(MessageCode.JOB_REPAIR_ALREADY_EXISTS, 409);
        }
        await publisher.PublishAsync("job", job.Id, ownerId, cancellationToken);
        return job;
    }

    /// <summary>原子取消当前排队状态或通知仍在运行的执行者，不修改已完成作业。</summary>
    public async Task CancelAsync(MediaJob job, CancellationToken cancellationToken = default)
    {
        var jobs = context.Collection<MediaJob>("jobs");
        var queued = await jobs.UpdateOneAsync(x => x.Id == job.Id && x.State == "Queued", Builders<MediaJob>.Update
            .Set(x => x.CancelRequested, true).Set(x => x.State, "Cancelled").Set(x => x.MutationKey, null)
            .Set(x => x.Error, null).Set(x => x.ErrorCode, null).Set(x => x.ErrorArguments, [])
            .Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntilUtc, null).Set(x => x.FinishedAtUtc, DateTime.UtcNow), cancellationToken: cancellationToken);
        if (queued.MatchedCount == 0)
        {
            await jobs.UpdateOneAsync(x => x.Id == job.Id && (x.State == "Running" || x.State == "Publishing"),
                Builders<MediaJob>.Update.Set(x => x.CancelRequested, true), cancellationToken: cancellationToken);
        }
        await publisher.PublishAsync("job", job.Id, job.OwnerId, cancellationToken);
    }

    /// <summary>基于当前终态重新入队，并重新取得场次修复互斥键。</summary>
    public async Task RetryAsync(MediaJob job, CancellationToken cancellationToken = default)
    {
        await EnsureMutationIndexAsync(cancellationToken);
        var current = await context.Collection<MediaJob>("jobs").Find(x => x.Id == job.Id).FirstOrDefaultAsync(cancellationToken)
                      ?? throw new InfrastructureException(MessageCode.JOB_NOT_FOUND, 404);
        if (current.State is not ("Failed" or "Cancelled"))
        {
            throw new InfrastructureException(MessageCode.JOB_RETRY_STATE_INVALID, 409);
        }
        if (current.Kind == "Repair")
        {
            var detail = await library.GetAsync(current.SessionId, cancellationToken);
            if (detail.Session.MediaRevision != current.SourceRevision
                && !(detail.Session.MediaRevision == current.SourceRevision + 1 && detail.Session.MediaRevisionJobId == current.Id))
            {
                throw new InfrastructureException(MessageCode.JOB_SOURCE_REVISION_CHANGED, 409);
            }
        }
        try
        {
            var updated = await context.Collection<MediaJob>("jobs").UpdateOneAsync(x => x.Id == current.Id && x.Attempt == current.Attempt && (x.State == "Failed" || x.State == "Cancelled"),
                Builders<MediaJob>.Update.Set(x => x.State, "Queued").Set(x => x.Error, null).Set(x => x.ErrorCode, null).Set(x => x.ErrorArguments, []).Set(x => x.Progress, 0).Set(x => x.CancelRequested, false)
                    .Set(x => x.FinishedAtUtc, null).Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntilUtc, null)
                    .Set(x => x.MutationKey, current.Kind == "Repair" ? current.SessionId : null), cancellationToken: cancellationToken);
            if (updated.MatchedCount == 0)
            {
                throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
            }
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InfrastructureException(MessageCode.JOB_OTHER_REPAIR_EXISTS, 409);
        }
        await publisher.PublishAsync("job", current.Id, current.OwnerId, cancellationToken);
    }

    private Task EnsureMutationIndexAsync(CancellationToken cancellationToken)
    {
        return context.Collection<MediaJob>("jobs").Indexes.CreateOneAsync(new CreateIndexModel<MediaJob>(
            Builders<MediaJob>.IndexKeys.Ascending(x => x.MutationKey), new CreateIndexOptions<MediaJob>
            {
                Name = "active_mutation",
                Unique = true,
                PartialFilterExpression = new BsonDocument("MutationKey", new BsonDocument("$type", "string"))
            }), cancellationToken: cancellationToken);
    }
}
