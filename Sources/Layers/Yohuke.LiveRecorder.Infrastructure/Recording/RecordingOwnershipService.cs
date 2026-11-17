using System.Globalization;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Arbitrates persistent room ownership shared by local capture and disconnected remote Workers.</summary>
public sealed class RecordingOwnershipService(IMongoContext context, IAppPaths paths, ISecretProtector protector)
{
    /// <summary>Reserves a room to a local process without stealing a disconnected remote reservation.</summary>
    public Task<RecordingAssignmentRecord?> TryAcquireLocalAsync(Streamer target, string ownerId, CancellationToken cancellationToken) =>
        TryAcquireAsync(target, "Local", ownerId, cancellationToken);

    /// <summary>Reserves a released room to one remotely authenticated Worker.</summary>
    public Task<RecordingAssignmentRecord?> TryAcquireWorkerAsync(Streamer target, string workerId, CancellationToken cancellationToken) =>
        TryAcquireAsync(target, "Worker", workerId, cancellationToken);

    /// <summary>Reads the durable reservation independently of any last-seen or connection timeout.</summary>
    public async Task<RecordingOwnership?> GetAsync(long roomId, CancellationToken cancellationToken = default) =>
        await Rooms.Find(value => value.Id == RoomKey(roomId)).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Validates current or released historical ingestion identity, including disabled recording nodes allowed to drain.</summary>
    public async Task<RecordingAssignmentRecord> RequireAsync(string workerId, string assignmentId, int generation, string token,
        CancellationToken cancellationToken, bool allowReleased = false)
    {
        if (generation <= 0 || token.Length is < 1 or > 256)
        {
            throw new InfrastructureException("录像归属凭据无效。", 409);
        }
        var worker = await context.Collection<TranscodeWorker>("transcodeWorkers")
            .Find(value => value.Id == workerId && value.Kind == "Recording").FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("录像节点不存在。", 401);
        var hash = TranscodeWorkerService.SecretHash(token);
        var assignment = await Assignments.Find(value => value.Id == assignmentId && value.OwnerKind == "Worker"
            && value.OwnerId == worker.Id && value.Generation == generation && value.TokenHash == hash
            && (value.State == "Owned" || value.State == "StopRequested" || allowReleased && value.State == "Released"))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("录像归属已改变。", 409);
        if (assignment.State != "Released")
        {
            var current = await GetAsync(assignment.RoomId, cancellationToken);
            if (allowReleased && current is not null && current.Generation >= generation
                && (current.AssignmentId != assignment.Id || current.State == "Released"))
            {
                assignment.State = "Released";
                await Assignments.UpdateOneAsync(value => value.Id == assignment.Id,
                    Builders<RecordingAssignmentRecord>.Update.Set(value => value.State, "Released"), cancellationToken: cancellationToken);
                return assignment;
            }
            if (current?.AssignmentId != assignment.Id || current.Generation != generation || current.State == "Released")
            {
                throw new InfrastructureException("录像房间已归属其它执行代次。", 409);
            }
        }
        return assignment;
    }

    /// <summary>Returns all persistent room reservations still owned by a recording node.</summary>
    public async Task<IReadOnlyList<RecordingAssignmentRecord>> ListWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        var rooms = await Rooms.Find(value => value.OwnerKind == "Worker" && value.OwnerId == workerId && value.State != "Released")
            .ToListAsync(cancellationToken);
        var ids = rooms.Select(value => value.AssignmentId).ToArray();
        var assignments = await Assignments.Find(value => ids.Contains(value.Id)).SortBy(value => value.CreatedAtUtc).ToListAsync(cancellationToken);
        return assignments.Where(value => rooms.Any(room => room.AssignmentId == value.Id && room.Generation == value.Generation)).ToList();
    }

    /// <summary>Marks a remote reservation for a drained stop without granting another recorder permission.</summary>
    public async Task RequestStopAsync(string assignmentId, CancellationToken cancellationToken)
    {
        await Assignments.UpdateOneAsync(value => value.Id == assignmentId && value.State == "Owned",
            Builders<RecordingAssignmentRecord>.Update.Set(value => value.State, "StopRequested"), cancellationToken: cancellationToken);
    }

    /// <summary>Releases local ownership only after its capture and event writers have exited.</summary>
    public async Task ReleaseLocalAsync(RecordingAssignmentRecord assignment, CancellationToken cancellationToken)
    {
        if (assignment.OwnerKind != "Local")
        {
            throw new InvalidOperationException("Remote recording ownership requires a drained stop acknowledgement.");
        }
        await ReleaseAsync(assignment, cancellationToken);
    }

    /// <summary>Releases a remote reservation after the ingestion service verified every declared session is sealed.</summary>
    public Task ReleaseWorkerAsync(RecordingAssignmentRecord assignment, CancellationToken cancellationToken) =>
        ReleaseAsync(assignment, cancellationToken);

    /// <summary>Chooses a compatible connected node with capacity, including reservations currently being handed off locally.</summary>
    public async Task<TranscodeWorker?> FindReadyWorkerAsync(CancellationToken cancellationToken)
    {
        var recent = DateTime.UtcNow.AddSeconds(-15);
        var workers = await context.Collection<TranscodeWorker>("transcodeWorkers").Find(value => value.Kind == "Recording"
            && value.Enabled && value.LastSeenAtUtc > recent && value.Capabilities.RecordingVersion >= 2
            && value.ConfigurationError == null).ToListAsync(cancellationToken);
        foreach (var worker in workers.Where(IsReady))
        {
            var active = await Rooms.CountDocumentsAsync(value => value.OwnerKind == "Worker" && value.OwnerId == worker.Id
                && value.State != "Released", cancellationToken: cancellationToken);
            var handingOff = await Rooms.CountDocumentsAsync(value => value.OwnerKind == "Local" && value.State != "Released"
                && value.HandoffWorkerId == worker.Id, cancellationToken: cancellationToken);
            if (active + handingOff < worker.Configuration.MaxConcurrentEncodes)
            {
                return worker;
            }
        }
        return null;
    }

    /// <summary>Reserves capacity before gracefully asking a local recorder to finish and hand its room to a node.</summary>
    public async Task<bool> TryRequestHandoffAsync(long roomId, string localOwnerId, CancellationToken cancellationToken)
    {
        var current = await GetAsync(roomId, cancellationToken);
        if (current?.OwnerKind != "Local" || current.OwnerId != localOwnerId || current.State == "Released")
        {
            return false;
        }
        if (current.HandoffWorkerId is not null)
        {
            return true;
        }
        var worker = await FindReadyWorkerAsync(cancellationToken);
        if (worker is null)
        {
            return false;
        }
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, worker.Id, cancellationToken);
        if ((await FindReadyWorkerAsync(cancellationToken))?.Id != worker.Id)
        {
            return false;
        }
        return (await Rooms.UpdateOneAsync(value => value.Id == current.Id && value.AssignmentId == current.AssignmentId
                && value.OwnerId == localOwnerId && value.State != "Released" && value.HandoffWorkerId == null,
            Builders<RecordingOwnership>.Update.Set(value => value.HandoffWorkerId, worker.Id), cancellationToken: cancellationToken)).MatchedCount > 0;
    }

    /// <summary>Returns the original assignment token only to its authenticated owner.</summary>
    public string Token(RecordingAssignmentRecord assignment) => protector.Unprotect(assignment.ProtectedToken);

    private IMongoCollection<RecordingOwnership> Rooms => context.Collection<RecordingOwnership>("recordingOwnerships");
    private IMongoCollection<RecordingAssignmentRecord> Assignments => context.Collection<RecordingAssignmentRecord>("recordingAssignments");

    private async Task<RecordingAssignmentRecord?> TryAcquireAsync(Streamer target, string kind, string ownerId, CancellationToken cancellationToken)
    {
        var key = RoomKey(target.RoomId);
        await using var roomLock = await MediaJobExecutionLock.AcquireAsync(paths, key, cancellationToken);
        var current = await GetAsync(target.RoomId, cancellationToken);
        if (current is null)
        {
            try
            {
                await Rooms.InsertOneAsync(new() { Id = key, RoomId = target.RoomId, TargetId = target.Id }, cancellationToken: cancellationToken);
            }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
            }
            current = await GetAsync(target.RoomId, cancellationToken);
        }
        if (current!.State != "Released")
        {
            return current.OwnerKind == kind && current.OwnerId == ownerId
                ? await Assignments.Find(value => value.Id == current.AssignmentId).FirstOrDefaultAsync(cancellationToken) : null;
        }
        var token = TranscodeWorkerService.NewSecret();
        var assignment = new RecordingAssignmentRecord
        {
            RoomId = target.RoomId, TargetId = target.Id, OwnerKind = kind, OwnerId = ownerId,
            Generation = checked(current.Generation + 1), TokenHash = TranscodeWorkerService.SecretHash(token),
            ProtectedToken = protector.Protect(token)
        };
        await Assignments.InsertOneAsync(assignment, cancellationToken: cancellationToken);
        var changed = await Rooms.UpdateOneAsync(value => value.Id == key && value.State == "Released" && value.Generation == current.Generation,
            Builders<RecordingOwnership>.Update.Set(value => value.AssignmentId, assignment.Id).Set(value => value.Generation, assignment.Generation)
                .Set(value => value.OwnerKind, kind).Set(value => value.OwnerId, ownerId).Set(value => value.State, "Owned")
                .Set(value => value.TargetId, target.Id).Set(value => value.LastSeenAtUtc, DateTime.UtcNow).Set(value => value.HandoffWorkerId, null),
            cancellationToken: cancellationToken);
        if (changed.MatchedCount > 0)
        {
            return assignment;
        }
        await Assignments.UpdateOneAsync(value => value.Id == assignment.Id,
            Builders<RecordingAssignmentRecord>.Update.Set(value => value.State, "Abandoned"), cancellationToken: cancellationToken);
        return null;
    }

    private async Task ReleaseAsync(RecordingAssignmentRecord assignment, CancellationToken cancellationToken)
    {
        await using var roomLock = await MediaJobExecutionLock.AcquireAsync(paths, RoomKey(assignment.RoomId), cancellationToken);
        await Rooms.UpdateOneAsync(value => value.AssignmentId == assignment.Id && value.Generation == assignment.Generation
                && value.OwnerId == assignment.OwnerId && value.State != "Released",
            Builders<RecordingOwnership>.Update.Set(value => value.State, "Released"), cancellationToken: cancellationToken);
        await Assignments.UpdateOneAsync(value => value.Id == assignment.Id && value.Generation == assignment.Generation,
            Builders<RecordingAssignmentRecord>.Update.Set(value => value.State, "Released"), cancellationToken: cancellationToken);
    }

    private static string RoomKey(long roomId) => "recording-room-" + roomId.ToString(CultureInfo.InvariantCulture);
    private static bool IsReady(TranscodeWorker worker) => worker.AppliedConfigurationRevision == worker.ConfigurationRevision
        && !string.IsNullOrWhiteSpace(worker.Configuration.TempDirectory)
        && worker.Telemetry.FreeBytes >= worker.Configuration.MinimumFreeBytes
        && worker.Telemetry.SpoolBytes < worker.Configuration.MaxCacheBytes;
}
