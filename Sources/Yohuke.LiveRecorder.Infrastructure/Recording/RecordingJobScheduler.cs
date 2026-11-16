using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Assigns durable room reservations and exchanges lightweight controls without tying capture to a connection lease.</summary>
public sealed class RecordingJobScheduler(IMongoContext context, IAppPaths paths, ISettingsStore settingsStore,
    TranscodeWorkerService workers, RecordingOwnershipService ownership, RecordingAccountProvider accounts, IStatusPublisher publisher,
    RecordingHandoffService handoff, RecordingTargetStatusService? targetStatus = null)
{
    /// <summary>Returns existing room controls even for disabled nodes, while only enabled configured nodes receive new rooms.</summary>
    public async Task<WorkerState> HeartbeatAsync(string workerId, WorkerHeartbeat heartbeat, CancellationToken cancellationToken,
        string? authenticatedSecretHash = null)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, workerId, cancellationToken);
        var worker = await workers.RequireAsync(workerId, cancellationToken, true);
        TranscodeWorkerService.RequireCredential(worker, authenticatedSecretHash);
        if (worker.Kind != "Recording")
        {
            throw new InfrastructureException("此节点不接受录像房间。", 409);
        }
        var update = Builders<TranscodeWorker>.Update.Set(value => value.LastSeenAtUtc, DateTime.UtcNow)
            .Set(value => value.Telemetry, heartbeat.Telemetry).Set(value => value.AppliedConfigurationRevision, heartbeat.AppliedConfigurationRevision)
            .Set(value => value.ConfigurationError, heartbeat.ConfigurationError);
        if (heartbeat.Capabilities is { } capabilities)
        {
            worker.Capabilities = capabilities;
            update = update.Set(value => value.Capabilities, capabilities);
        }
        await context.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == workerId, update, cancellationToken: cancellationToken);
        var state = new WorkerState { Configuration = worker.Configuration, ConfigurationRevision = worker.ConfigurationRevision };
        foreach (var progress in heartbeat.Recordings.Take(64))
        {
            try
            {
                var assignment = await ownership.RequireAsync(workerId, progress.AssignmentId, progress.Generation, progress.Token, cancellationToken);
                await Assignments.UpdateOneAsync(value => value.Id == assignment.Id && value.Generation == assignment.Generation,
                    Builders<RecordingAssignmentRecord>.Update.Set(value => value.LastSeenAtUtc, DateTime.UtcNow)
                        .Set(value => value.LastCapturedAtUtc, progress.LastCapturedAtUtc)
                        .Set(value => value.PendingUploadBytes, Math.Max(0, progress.PendingUploadBytes))
                        .Set(value => value.SpoolBytes, Math.Max(0, progress.SpoolBytes)).Set(value => value.CurrentSessionId, progress.SessionId)
                        .Set(value => value.Error, progress.Error is { Length: > 4096 } ? progress.Error[..4096] : progress.Error), cancellationToken: cancellationToken);
                await (targetStatus ?? new(context, publisher)).ApplyAsync(assignment, progress, cancellationToken);
            }
            catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
            {
            }
        }
        var owned = (await ownership.ListWorkerAsync(workerId, cancellationToken)).ToList();
        foreach (var excess in owned.Skip(worker.Configuration.MaxConcurrentEncodes))
        {
            await ownership.RequestStopAsync(excess.Id, cancellationToken);
            excess.State = "StopRequested";
        }
        if (worker.Enabled && worker.Capabilities.RecordingVersion >= 1 && heartbeat.ConfigurationError is null
            && heartbeat.AppliedConfigurationRevision == worker.ConfigurationRevision && !string.IsNullOrWhiteSpace(worker.Configuration.TempDirectory)
            && heartbeat.Telemetry.FreeBytes >= worker.Configuration.MinimumFreeBytes && heartbeat.Telemetry.SpoolBytes < worker.Configuration.MaxCacheBytes
            && owned.Count < worker.Configuration.MaxConcurrentEncodes)
        {
            foreach (var target in await context.Collection<Streamer>("streamers").Find(value => value.Enabled && value.IsMonitored)
                .SortBy(value => value.CreatedAtUtc).Limit(500).ToListAsync(cancellationToken))
            {
                var reserved = await ownership.TryAcquireWorkerAsync(target, workerId, cancellationToken);
                if (reserved is not null && owned.All(value => value.Id != reserved.Id))
                {
                    await handoff.CloseLocalSessionsAsync(target.Id, cancellationToken);
                    owned.Add(reserved);
                    break;
                }
            }
        }
        var settings = await settingsStore.GetAsync(cancellationToken);
        var accountVersion = await accounts.FingerprintAsync(cancellationToken);
        foreach (var assignment in owned)
        {
            var definition = await RefreshDefinitionAsync(assignment, worker, settings, accountVersion, cancellationToken);
            state.RecordingAssignments.Add(new()
            {
                AssignmentId = assignment.Id, Generation = assignment.Generation, Token = ownership.Token(assignment),
                RoomId = assignment.RoomId, Enabled = definition.Enabled, ConfigurationRevision = definition.ConfigurationRevision
            });
        }
        return state;
    }

    /// <summary>Serves the full offline capture policy and credentials through the separate authenticated HTTP data plane.</summary>
    public async Task<RecordingAssignment> GetAssignmentAsync(string workerId, string assignmentId, int generation, string token,
        CancellationToken cancellationToken, string? authenticatedSecret = null)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, workerId, cancellationToken);
        var assignment = await ownership.RequireAsync(workerId, assignmentId, generation, token, cancellationToken);
        var worker = await workers.RequireAsync(workerId, cancellationToken, true);
        TranscodeWorkerService.RequireCredential(worker, authenticatedSecret is null ? null : TranscodeWorkerService.SecretHash(authenticatedSecret));
        var definition = await RefreshDefinitionAsync(assignment, worker, await settingsStore.GetAsync(cancellationToken),
            await accounts.FingerprintAsync(cancellationToken), cancellationToken);
        var target = await context.Collection<Streamer>("streamers").Find(value => value.Id == assignment.TargetId).FirstOrDefaultAsync(cancellationToken);
        return definition with
        {
            Token = ownership.Token(assignment),
            Accounts = definition.Enabled ? await accounts.ReadAsync(target?.AccountId, cancellationToken) : []
        };
    }

    private IMongoCollection<RecordingAssignmentRecord> Assignments => context.Collection<RecordingAssignmentRecord>("recordingAssignments");

    private async Task<RecordingAssignment> RefreshDefinitionAsync(RecordingAssignmentRecord assignment, TranscodeWorker worker,
        ServerSettings settings, string accountVersion, CancellationToken cancellationToken)
    {
        var target = await context.Collection<Streamer>("streamers").Find(value => value.Id == assignment.TargetId).FirstOrDefaultAsync(cancellationToken);
        var enabled = worker.Enabled && target is { Enabled: true, IsMonitored: true } && assignment.State == "Owned";
        if (!enabled && assignment.State == "Owned")
        {
            await ownership.RequestStopAsync(assignment.Id, cancellationToken);
            assignment.State = "StopRequested";
        }
        var definition = new RecordingAssignment
        {
            AssignmentId = assignment.Id, Generation = assignment.Generation, WorkerId = assignment.OwnerId, TargetId = assignment.TargetId,
            RoomId = assignment.RoomId, Name = target?.Name ?? assignment.Definition?.Name ?? "", Enabled = enabled,
            Quality = target?.Quality ?? settings.Quality, SegmentBytes = target?.SegmentBytes ?? settings.SegmentBytes,
            RecordDanmu = target?.RecordDanmu ?? settings.RecordDanmu, SessionGraceMinutes = settings.SessionGraceMinutes,
            RecordingTimeoutMinutes = settings.StreamDisconnectTimeoutMinutes, MonitorIntervalSeconds = settings.MonitorIntervalSeconds,
            TimeZone = settings.TimeZone
        };
        var fingerprint = TranscodeAssetRegistry.Hash(JsonSerializer.Serialize(definition with { Name = "" }, WorkerProtocol.JsonOptions)
            + ":" + accountVersion + ":" + target?.AccountId + ":" + settings.DefaultBilibiliAccountId);
        if (assignment.Definition is null || fingerprint != assignment.ConfigurationFingerprint)
        {
            assignment.ConfigurationRevision++;
            definition = definition with { ConfigurationRevision = assignment.ConfigurationRevision };
            assignment.Definition = definition;
            assignment.ConfigurationFingerprint = fingerprint;
            await Assignments.UpdateOneAsync(value => value.Id == assignment.Id && value.Generation == assignment.Generation,
                Builders<RecordingAssignmentRecord>.Update.Set(value => value.Definition, definition)
                    .Set(value => value.ConfigurationRevision, assignment.ConfigurationRevision).Set(value => value.ConfigurationFingerprint, fingerprint),
                cancellationToken: cancellationToken);
        }
        return definition with { ConfigurationRevision = assignment.ConfigurationRevision };
    }
}
