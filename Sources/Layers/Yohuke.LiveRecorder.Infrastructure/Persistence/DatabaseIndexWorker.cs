using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>幂等建立管理集合索引，不为弹幕创建数据库集合。</summary>
public sealed class DatabaseIndexWorker(IMongoContext context, IBootstrapStore bootstrap, ILogger<DatabaseIndexWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (bootstrap.IsReady)
                {
                    await context.Collection<Streamer>("streamers").Indexes.CreateOneAsync(new CreateIndexModel<Streamer>(Builders<Streamer>.IndexKeys.Ascending(x => x.RoomId), new CreateIndexOptions { Unique = true, Name = "room_unique" }), cancellationToken: stoppingToken);
                    await context.Collection<BroadcastSession>("sessions").Indexes.CreateOneAsync(new CreateIndexModel<BroadcastSession>(Builders<BroadcastSession>.IndexKeys.Ascending(x => x.StreamerId).Descending(x => x.CreatedAtUtc)), cancellationToken: stoppingToken);
                    await context.Collection<RecordingSegment>("segments").Indexes.CreateOneAsync(new CreateIndexModel<RecordingSegment>(Builders<RecordingSegment>.IndexKeys.Ascending(x => x.SessionId).Ascending(x => x.Revision).Ascending(x => x.StartOffsetMs)), cancellationToken: stoppingToken);
                    await context.Collection<RecordingOwnership>("recordingOwnerships").Indexes.CreateOneAsync(new CreateIndexModel<RecordingOwnership>(
                        Builders<RecordingOwnership>.IndexKeys.Ascending(value => value.OwnerKind).Ascending(value => value.OwnerId).Ascending(value => value.State)), cancellationToken: stoppingToken);
                    await context.Collection<RecordingAssignmentRecord>("recordingAssignments").Indexes.CreateOneAsync(new CreateIndexModel<RecordingAssignmentRecord>(
                        Builders<RecordingAssignmentRecord>.IndexKeys.Ascending(value => value.OwnerId).Ascending(value => value.State)), cancellationToken: stoppingToken);
                    await context.Collection<RecordingSessionReceipt>("recordingSessions").Indexes.CreateOneAsync(new CreateIndexModel<RecordingSessionReceipt>(
                        Builders<RecordingSessionReceipt>.IndexKeys.Ascending(value => value.AssignmentId)), cancellationToken: stoppingToken);
                    await context.Collection<RecordingCaptureBinding>("recordingCaptureBindings").Indexes.CreateOneAsync(new CreateIndexModel<RecordingCaptureBinding>(
                        Builders<RecordingCaptureBinding>.IndexKeys.Ascending(value => value.LogicalSessionId).Ascending(value => value.StartedAtUtc)), cancellationToken: stoppingToken);
                    await context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").Indexes.CreateOneAsync(new CreateIndexModel<RecordingSessionTaskSnapshot>(
                        Builders<RecordingSessionTaskSnapshot>.IndexKeys.Ascending(value => value.WorkerId).Descending(value => value.CreatedAtUtc)), cancellationToken: stoppingToken);
                    await context.Collection<TaskHistoryDismissal>("taskHistoryDismissals").Indexes.CreateOneAsync(new CreateIndexModel<TaskHistoryDismissal>(
                        Builders<TaskHistoryDismissal>.IndexKeys.Ascending(value => value.Scope).Ascending(value => value.ViewerId).Descending(value => value.CreatedAtUtc)), cancellationToken: stoppingToken);
                    await context.Collection<RecordingSegmentReceipt>("recordingSegments").Indexes.CreateOneAsync(new CreateIndexModel<RecordingSegmentReceipt>(
                        Builders<RecordingSegmentReceipt>.IndexKeys.Ascending(value => value.SessionId).Ascending(value => value.AssignmentId)), cancellationToken: stoppingToken);
                    await context.Collection<SessionBookmark>("sessionBookmarks").Indexes.CreateOneAsync(new CreateIndexModel<SessionBookmark>(
                        Builders<SessionBookmark>.IndexKeys.Ascending(x => x.SessionId).Ascending(x => x.OwnerId).Ascending(x => x.OffsetMs).Ascending(x => x.Id),
                        new CreateIndexOptions { Name = "session_bookmarks_session_owner_offset" }), cancellationToken: stoppingToken);
                    await context.Collection<MediaJob>("jobs").Indexes.CreateOneAsync(new CreateIndexModel<MediaJob>(Builders<MediaJob>.IndexKeys.Ascending(x => x.State).Ascending(x => x.CreatedAtUtc)), cancellationToken: stoppingToken);
                    await context.Collection<MediaJob>("jobs").Indexes.CreateOneAsync(new CreateIndexModel<MediaJob>(
                        Builders<MediaJob>.IndexKeys.Ascending(x => x.Kind).Ascending(x => x.State).Ascending(x => x.LeaseUntilUtc).Ascending(x => x.CreatedAtUtc),
                        new CreateIndexOptions { Name = "clipping_queue" }), cancellationToken: stoppingToken);
                    await context.Collection<MediaJob>("jobs").Indexes.CreateOneAsync(new CreateIndexModel<MediaJob>(
                        Builders<MediaJob>.IndexKeys.Ascending(x => x.RemoteWorkerId).Ascending(x => x.State).Ascending(x => x.LeaseUntilUtc),
                        new CreateIndexOptions { Name = "clipping_worker_jobs" }), cancellationToken: stoppingToken);
                    await context.Collection<MediaJob>("jobs").Indexes.CreateOneAsync(new CreateIndexModel<MediaJob>(Builders<MediaJob>.IndexKeys.Ascending(x => x.MutationKey), new CreateIndexOptions<MediaJob> { Unique = true, Name = "active_mutation", PartialFilterExpression = new BsonDocument("MutationKey", new BsonDocument("$type", "string")) }), cancellationToken: stoppingToken);
                    await context.Collection<Clip>("clips").Indexes.CreateOneAsync(new CreateIndexModel<Clip>(Builders<Clip>.IndexKeys.Ascending(x => x.OwnerId).Ascending(x => x.IsTrashed).Ascending(x => x.ExpiresAtUtc)), cancellationToken: stoppingToken);
                    await context.Collection<Clip>("clips").Indexes.CreateOneAsync(new CreateIndexModel<Clip>(
                        Builders<Clip>.IndexKeys.Ascending(x => x.IsTrashed).Descending(x => x.CreatedAtUtc).Descending(x => x.Id),
                        new CreateIndexOptions { Name = "clips_management_created" }), cancellationToken: stoppingToken);
                    await context.Collection<TrashEntry>("trash").Indexes.CreateOneAsync(new CreateIndexModel<TrashEntry>(Builders<TrashEntry>.IndexKeys.Ascending(x => x.EntityId), new CreateIndexOptions { Unique = true, Name = "trash_entity_unique" }), cancellationToken: stoppingToken);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("Database index initialization will resume after recovery: {Message}", error.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
