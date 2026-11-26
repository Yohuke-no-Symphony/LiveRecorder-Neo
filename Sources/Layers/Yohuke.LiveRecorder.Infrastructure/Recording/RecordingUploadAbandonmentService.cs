using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>持久化单个物理场次的放弃补传授权及清理回执。</summary>
public sealed class RecordingUploadAbandonmentService(IMongoContext context, IAppPaths paths)
{
    private IMongoCollection<RecordingUploadAbandonment> Requests => context.Collection<RecordingUploadAbandonment>("recordingUploadAbandonments");
    private const int MAX_COMMANDS = 16;

    /// <summary>由管理员授权 Worker 放弃尚未完整签收的已关闭物理场次。</summary>
    public async Task RequestAsync(string sessionId, TaskHistoryViewer viewer, CancellationToken cancellationToken)
    {
        if (!viewer.IsAdmin || string.IsNullOrWhiteSpace(viewer.UserId))
        {
            throw new InfrastructureException(MessageCode.REQUEST_FORBIDDEN, 403);
        }
        RecordingIngestPaths.RequireId(sessionId);
        var snapshots = context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks");
        var observed = await snapshots.Find(value => value.Id == sessionId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException(MessageCode.REQUEST_ENDPOINT_NOT_FOUND, 404);
        if (string.IsNullOrWhiteSpace(observed.WorkerId) || string.IsNullOrWhiteSpace(observed.AssignmentId) || observed.Generation <= 0)
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }

        await using var ingestLock = await MediaJobExecutionLock.AcquireAsync(paths, RecordingIngestPaths.UploadLockKey(sessionId), cancellationToken);
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, observed.WorkerId, cancellationToken);
        var snapshot = await snapshots.Find(value => value.Id == sessionId && value.WorkerId == observed.WorkerId
            && value.AssignmentId == observed.AssignmentId && value.Generation == observed.Generation).FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null)
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }
        var existing = await Requests.Find(value => value.Id == sessionId).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.WorkerId != snapshot.WorkerId || existing.AssignmentId != snapshot.AssignmentId || existing.Generation != snapshot.Generation)
            {
                throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
            }
            return;
        }
        if (snapshot.Descriptor.SessionId != sessionId || !snapshot.Descriptor.IsClosed || snapshot.Descriptor.EndedAtUtc is null
            || !await context.Collection<RecordingAssignmentRecord>("recordingAssignments").Find(value => value.Id == snapshot.AssignmentId
                && value.Generation == snapshot.Generation && value.OwnerKind == "Worker" && value.OwnerId == snapshot.WorkerId
                && value.RoomId == snapshot.Descriptor.RoomId).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }
        var worker = await context.Collection<TranscodeWorker>("transcodeWorkers").Find(value => value.Id == snapshot.WorkerId && value.Kind == "Recording")
            .FirstOrDefaultAsync(cancellationToken);
        if (worker is null || worker.Capabilities.RecordingUploadAbandonmentVersion < 1)
        {
            throw new InfrastructureException(MessageCode.RECORDING_UPLOAD_ABANDONMENT_UNSUPPORTED, 409);
        }
        var receipt = await context.Collection<RecordingSessionReceipt>("recordingSessions").Find(value => value.Id == sessionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (receipt is not null && (receipt.AssignmentId != snapshot.AssignmentId || receipt.Generation != snapshot.Generation
            || receipt.Descriptor.RoomId != snapshot.Descriptor.RoomId || receipt.Descriptor.IsClosed || receipt.FinalizedAtUtc is not null))
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }
        await Requests.InsertOneAsync(new()
        {
            Id = sessionId, WorkerId = snapshot.WorkerId, AssignmentId = snapshot.AssignmentId,
            Generation = snapshot.Generation, RequestedByUserId = viewer.UserId
        }, cancellationToken: cancellationToken);
    }

    internal async Task RequireUploadAllowedAsync(RecordingAssignmentRecord assignment, string sessionId, CancellationToken cancellationToken)
    {
        if (await FindAsync(assignment, sessionId, cancellationToken) is not null)
        {
            throw new InfrastructureException(MessageCode.RECORDING_UPLOAD_ABANDONED, 410);
        }
    }

    internal async Task<List<RecordingUploadAbandonmentCommand>> PendingAsync(string workerId, RecordingOwnershipService ownership,
        CancellationToken cancellationToken)
    {
        var pending = await Requests.Find(value => value.WorkerId == workerId && value.AcknowledgedAtUtc == null)
            .SortBy(value => value.CreatedAtUtc).ThenBy(value => value.Id).Limit(MAX_COMMANDS).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return [];
        }
        var ids = pending.Select(value => value.AssignmentId).ToArray();
        var assignments = await context.Collection<RecordingAssignmentRecord>("recordingAssignments").Find(value => ids.Contains(value.Id)
            && value.OwnerKind == "Worker" && value.OwnerId == workerId).ToListAsync(cancellationToken);
        var commands = new List<RecordingUploadAbandonmentCommand>(pending.Count);
        foreach (var request in pending)
        {
            var assignment = assignments.Find(value => value.Id == request.AssignmentId && value.Generation == request.Generation);
            if (assignment is not null)
            {
                commands.Add(new() { SessionId = request.Id, AssignmentId = request.AssignmentId, Generation = request.Generation, Token = ownership.Token(assignment) });
            }
        }
        return commands;
    }

    internal async Task<RecordingUploadAbandonment?> FindAsync(RecordingAssignmentRecord assignment, string sessionId, CancellationToken cancellationToken) =>
        await Requests.Find(ForAssignment(assignment) & Builders<RecordingUploadAbandonment>.Filter.Eq(value => value.Id, sessionId))
            .FirstOrDefaultAsync(cancellationToken);

    internal async Task<List<RecordingUploadAbandonment>> ForAssignmentAsync(RecordingAssignmentRecord assignment, CancellationToken cancellationToken) =>
        await Requests.Find(ForAssignment(assignment)).ToListAsync(cancellationToken);

    internal async Task<List<RecordingUploadAbandonment>> ForCapturesAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancellationToken) =>
        await Requests.Find(value => captureIds.Contains(value.Id)).ToListAsync(cancellationToken);

    internal async Task AcknowledgeAsync(RecordingUploadAbandonment request, CancellationToken cancellationToken) =>
        await Requests.UpdateOneAsync(value => value.Id == request.Id && value.WorkerId == request.WorkerId
            && value.AssignmentId == request.AssignmentId && value.Generation == request.Generation && value.AcknowledgedAtUtc == null,
            Builders<RecordingUploadAbandonment>.Update.Set(value => value.AcknowledgedAtUtc, DateTime.UtcNow), cancellationToken: cancellationToken);

    private static FilterDefinition<RecordingUploadAbandonment> ForAssignment(RecordingAssignmentRecord assignment) =>
        Builders<RecordingUploadAbandonment>.Filter.Where(value => value.AssignmentId == assignment.Id
            && value.Generation == assignment.Generation && value.WorkerId == assignment.OwnerId);
}
