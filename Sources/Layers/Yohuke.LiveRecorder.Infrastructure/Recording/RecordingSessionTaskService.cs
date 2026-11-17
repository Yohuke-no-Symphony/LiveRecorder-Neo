using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Stores bounded authenticated worker observations separately from archive ingestion and library publication.</summary>
public sealed class RecordingSessionTaskService(IMongoContext context, RecordingOwnershipService ownership)
{
    /// <summary>Updates only the reported broadcasts; omitted entries in a bounded batch remain unchanged.</summary>
    public async Task ReportAsync(string workerId, IReadOnlyList<RecordingSessionProgress> progress, CancellationToken cancellationToken)
    {
        if (progress.Count > RecordingSessionProgress.MAX_BATCH_COUNT)
        {
            throw new InfrastructureException("录像场次进度超过单次心跳上限。");
        }
        var assignments = new Dictionary<string, RecordingAssignmentRecord>(StringComparer.Ordinal);
        var snapshots = context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks");
        foreach (var item in progress)
        {
            Validate(item);
            var identity = item.AssignmentId + ":" + item.Generation + ":" + item.Token;
            if (!assignments.TryGetValue(identity, out var assignment))
            {
                assignment = await ownership.RequireAsync(workerId, item.AssignmentId, item.Generation, item.Token,
                    cancellationToken, allowReleased: true);
                assignments.Add(identity, assignment);
            }
            if (item.Descriptor.RoomId != assignment.RoomId)
            {
                throw new InfrastructureException("录像场次进度不属于当前房间。", 409);
            }
            var previous = await snapshots.Find(value => value.Id == item.Descriptor.SessionId).FirstOrDefaultAsync(cancellationToken);
            if (previous is not null && (previous.WorkerId != workerId || previous.AssignmentId != assignment.Id
                || previous.Generation != assignment.Generation
                || previous.Descriptor.StartedAtUtc.Ticks / TimeSpan.TicksPerMillisecond != item.Descriptor.StartedAtUtc.Ticks / TimeSpan.TicksPerMillisecond
                || previous.Descriptor.LiveTime != item.Descriptor.LiveTime
                || (previous.Descriptor.LogicalSessionId ?? previous.Descriptor.SessionId) != (item.Descriptor.LogicalSessionId ?? item.Descriptor.SessionId)
                || (previous.Descriptor.LogicalStartedAtUtc ?? previous.Descriptor.StartedAtUtc).Ticks / TimeSpan.TicksPerMillisecond
                    != (item.Descriptor.LogicalStartedAtUtc ?? item.Descriptor.StartedAtUtc).Ticks / TimeSpan.TicksPerMillisecond))
            {
                throw new InfrastructureException("录像场次进度身份已改变。", 409);
            }
            var snapshot = new RecordingSessionTaskSnapshot
            {
                Id = item.Descriptor.SessionId, CreatedAtUtc = previous?.CreatedAtUtc ?? DateTime.UtcNow,
                WorkerId = workerId, AssignmentId = assignment.Id, Generation = assignment.Generation, TargetId = assignment.TargetId,
                Descriptor = item.Descriptor, CaptureState = item.CaptureState, CapturedDurationMs = item.CapturedDurationMs,
                CommittedArchiveBytes = item.CommittedArchiveBytes, AcknowledgedArchiveBytes = item.AcknowledgedArchiveBytes,
                LastCapturedAtUtc = item.LastCapturedAtUtc, UploadState = item.UploadState, CurrentTransfer = item.CurrentTransfer,
                ErrorCode = item.ErrorCode, LastReportedAtUtc = DateTime.UtcNow
            };
            try
            {
                await snapshots.ReplaceOneAsync(value => value.Id == snapshot.Id && value.WorkerId == workerId
                    && value.AssignmentId == assignment.Id && value.Generation == assignment.Generation,
                    snapshot, new ReplaceOptions { IsUpsert = true }, cancellationToken);
            }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new InfrastructureException("录像场次进度身份已被使用。", 409);
            }
        }
    }

    private static void Validate(RecordingSessionProgress item)
    {
        RecordingIngestPaths.RequireId(item.Descriptor.SessionId);
        var descriptor = item.Descriptor;
        if (descriptor.LogicalSessionId is not null)
        {
            RecordingIngestPaths.RequireId(descriptor.LogicalSessionId);
            if (descriptor.LogicalStartedAtUtc is not { Kind: DateTimeKind.Utc } logicalStarted || logicalStarted > descriptor.StartedAtUtc)
            {
                throw new InfrastructureException("逻辑场次开始时间无效。", 409);
            }
        }
        if (descriptor.RoomId <= 0 || descriptor.StartedAtUtc == default || descriptor.Title.Length > 1024
            || descriptor.StreamerName.Length > 256 || descriptor.CoverUrl.Length > 4096
            || descriptor.IsClosed && descriptor.EndedAtUtc is null || descriptor.EndedAtUtc < descriptor.StartedAtUtc
            || !double.IsFinite(item.CapturedDurationMs) || item.CapturedDurationMs < 0
            || item.CommittedArchiveBytes < 0 || item.AcknowledgedArchiveBytes < 0
            || item.AcknowledgedArchiveBytes > item.CommittedArchiveBytes
            || item.CaptureState is not ("Monitoring" or "Recording" or "Retrying" or "StorageFull" or "Paused" or "Draining" or "Closed" or "NoMedia")
            || item.UploadState is not ("Queued" or "Uploading" or "CaughtUp" or "Retrying" or "Finalizing" or "Skipped")
            || item.CaptureState == "NoMedia" && (!descriptor.IsClosed || item.CommittedArchiveBytes != 0)
            || item.ErrorCode is not (null or "RecordingUploadFailed" or "RecordingSourceUnavailable" or "RecordingStorageFull")
            || item.CurrentTransfer is { } transfer && (transfer.BytesTransferred < 0 || transfer.TotalBytes < 0
                || transfer.BytesTransferred > transfer.TotalBytes || transfer.ArtifactKind is not ("media" or "index" or "prefix" or "events" or "event-index")))
        {
            throw new InfrastructureException("录像场次进度无效。");
        }
    }
}
