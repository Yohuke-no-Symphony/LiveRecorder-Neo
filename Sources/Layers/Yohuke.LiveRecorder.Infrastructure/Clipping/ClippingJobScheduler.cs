using MongoDB.Driver;
using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Assigns existing media jobs to available clipping Workers without competing with local execution.</summary>
public sealed class ClippingJobScheduler(IMongoContext context, TranscodeWorkerService workers, ClipInputPlanner inputs,
    ClipLeaseService leases, IAppPaths paths, IStatusPublisher publisher, ClipPlanPreparation preparation)
{
    /// <summary>Renews active executions and assigns at most one new bounded clipping job.</summary>
    public async Task<WorkerState> HeartbeatAsync(string workerId, WorkerHeartbeat heartbeat, CancellationToken cancellationToken,
        string? authenticatedSecretHash = null, Func<bool>? connectionIsCurrent = null)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, workerId, cancellationToken);
        var worker = await workers.RequireAsync(workerId, cancellationToken);
        TranscodeWorkerService.RequireCredential(worker, authenticatedSecretHash);
        TranscodeWorkerService.RequireControlConnection(connectionIsCurrent);
        await leases.RequireWorkerAsync(workerId, cancellationToken);
        var update = Builders<TranscodeWorker>.Update.Set(value => value.LastSeenAtUtc, DateTime.UtcNow)
            .Set(value => value.Telemetry, heartbeat.Telemetry)
            .Set(value => value.AppliedConfigurationRevision, heartbeat.AppliedConfigurationRevision)
            .Set(value => value.ConfigurationError, heartbeat.ConfigurationError);
        if (heartbeat.Capabilities is { } capabilities)
        {
            update = update.Set(value => value.Capabilities, capabilities);
            worker.Capabilities = capabilities;
        }
        await context.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == workerId && value.Enabled,
            update, cancellationToken: cancellationToken);
        worker.Telemetry = heartbeat.Telemetry;
        worker.AppliedConfigurationRevision = heartbeat.AppliedConfigurationRevision;
        worker.ConfigurationError = heartbeat.ConfigurationError;
        var state = new WorkerState { Configuration = worker.Configuration, ConfigurationRevision = worker.ConfigurationRevision };
        foreach (var recovery in heartbeat.RecoveryRequests.Take(65))
        {
            state.RecoveryResults.Add(await leases.RecoverAsync(workerId, recovery, paths, cancellationToken));
        }
        foreach (var progress in heartbeat.Jobs.Take(65))
        {
            var accepted = false;
            try
            {
                var activeJob = await leases.RequireAsync(workerId, progress.JobId, progress.Attempt, progress.LeaseToken, cancellationToken);
                var progressUpdate = Builders<MediaJob>.Update.Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(90))
                    .Set(value => value.LastProgressAtUtc, DateTime.UtcNow)
                    .Set(value => value.Progress, double.IsFinite(progress.Progress) ? Math.Clamp(progress.Progress, 0, 99.9) : 0);
                var stages = WorkerStageSnapshotPolicy.Normalize("Clipping", progress.Stages);
                if (stages.Count > 0)
                {
                    progressUpdate = progressUpdate.Set(value => value.Stages, stages);
                }
                var changed = await Jobs.UpdateOneAsync(ClipLeaseService.Fence(workerId, progress.JobId, progress.Attempt, progress.LeaseToken),
                    progressUpdate, cancellationToken: cancellationToken);
                accepted = changed.MatchedCount > 0;
                if (accepted)
                {
                    await publisher.PublishAsync("job", progress.JobId, activeJob.OwnerId, cancellationToken);
                }
            }
            catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
            {
                accepted = await leases.IsFinalizingAsync(workerId, progress.JobId, progress.Attempt, progress.LeaseToken, cancellationToken);
            }
            state.Leases.Add(new() { JobId = progress.JobId, Attempt = progress.Attempt, Accepted = accepted });
        }
        if (!IsConfigured(worker) || await ActiveCountAsync(worker.Id, cancellationToken) >= worker.Configuration.MaxConcurrentEncodes)
        {
            return state;
        }
        foreach (var candidate in (await PendingAsync(cancellationToken)).OrderByDescending(job => job.ResumeWorkerId == workerId))
        {
            if (HasCompletedOutput(candidate))
            {
                continue;
            }
            if (!preparation.TryGetReady(candidate, out var plan))
            {
                preparation.Request(candidate);
                continue;
            }
            if (!CanAssign(worker, plan!.Assignment, state))
            {
                continue;
            }
            if (candidate.ResumeWorkerId != workerId && await HasPreferredCapacityAsync(candidate, cancellationToken))
            {
                continue;
            }
            candidate.RemoteAssignment = plan.Assignment;
            candidate.RemoteSources = plan.Sources;
            try
            {
                await inputs.ValidateSourcesAsync(candidate, cancellationToken);
            }
            catch (InfrastructureException error) when (error.StatusCode is 404 or 409 or 410)
            {
                preparation.Invalidate(candidate);
                continue;
            }
            var token = TranscodeWorkerService.NewSecret();
            var assignment = plan.Assignment with { Attempt = candidate.Attempt + 1, CheckpointGeneration = candidate.CheckpointGeneration };
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, candidate.Id, cancellationToken);
            var job = await Jobs.FindOneAndUpdateAsync(PendingFilter() & Builders<MediaJob>.Filter.Eq(value => value.Id, candidate.Id)
                    & Builders<MediaJob>.Filter.Eq(value => value.Attempt, candidate.Attempt),
                Builders<MediaJob>.Update.Set(value => value.State, "Running").Set(value => value.Progress, 0)
                    .Set(value => value.LeaseOwner, workerId).Set(value => value.RemoteWorkerId, workerId)
                    .Set(value => value.ResumeWorkerId, workerId)
                    .Set(value => value.RemoteLeaseTokenHash, TranscodeWorkerService.SecretHash(token))
                    .Set(value => value.RemoteAssignment, assignment).Set(value => value.RemoteSources, plan.Sources)
                    .Set(value => value.RemoteOutputSha256, null).Set(value => value.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(90))
                    .Inc(value => value.Attempt, 1),
                new FindOneAndUpdateOptions<MediaJob> { ReturnDocument = ReturnDocument.After }, cancellationToken);
            if (job is null)
            {
                continue;
            }
            state.ClipAssignments.Add(assignment with { LeaseToken = token });
            await publisher.PublishAsync("job", job.Id, job.OwnerId, cancellationToken);
            break;
        }
        return state;
    }

    /// <summary>Returns jobs the local scheduler should defer while a compatible remote Worker has capacity.</summary>
    public async Task<IReadOnlyCollection<string>> GetRemoteEligibleJobIdsAsync(CancellationToken cancellationToken)
    {
        var recent = DateTime.UtcNow.AddSeconds(-10);
        var candidates = await context.Collection<TranscodeWorker>("transcodeWorkers")
            .Find(value => value.Enabled && value.Kind == "Clipping" && value.LastSeenAtUtc > recent).ToListAsync(cancellationToken);
        var available = new List<TranscodeWorker>();
        foreach (var worker in candidates.Where(IsConfigured))
        {
            if (await ActiveCountAsync(worker.Id, cancellationToken) < worker.Configuration.MaxConcurrentEncodes)
            {
                available.Add(worker);
            }
        }
        if (available.Count == 0)
        {
            return [];
        }
        var result = new List<string>();
        foreach (var job in await PendingAsync(cancellationToken))
        {
            if (HasCompletedOutput(job))
            {
                continue;
            }
            var plan = await inputs.TryPlanAsync(job, cancellationToken, false);
            if (plan is not null && !preparation.HasFailed(job) && available.Any(worker => CanAssign(worker, plan.Assignment))
                && preparation.Request(job))
            {
                result.Add(job.Id);
            }
        }
        return result;
    }

    private IMongoCollection<MediaJob> Jobs => context.Collection<MediaJob>("jobs");

    /// <summary>在全部认领路径中保护在线且有容量的原节点，失去条件时立即允许其他执行者接管。</summary>
    public async Task<bool> HasPreferredCapacityAsync(MediaJob job, CancellationToken cancellationToken)
    {
        if (job.ResumeWorkerId is not { Length: > 0 } preferredId)
        {
            return false;
        }
        var recent = DateTime.UtcNow.AddSeconds(-10);
        var worker = await context.Collection<TranscodeWorker>("transcodeWorkers")
            .Find(value => value.Id == preferredId && value.Enabled && value.LastSeenAtUtc > recent).FirstOrDefaultAsync(cancellationToken);
        if (worker is null || !IsConfigured(worker) || await ActiveCountAsync(preferredId, cancellationToken) >= worker.Configuration.MaxConcurrentEncodes)
        {
            return false;
        }
        var assignment = job.RemoteAssignment;
        if (assignment is null && preparation.TryGetReady(job, out var plan))
        {
            assignment = plan!.Assignment;
        }
        return assignment is not null && CanAssign(worker, assignment);
    }

    private async Task<long> ActiveCountAsync(string workerId, CancellationToken cancellationToken) =>
        await Jobs.CountDocumentsAsync(value => value.RemoteWorkerId == workerId && (value.State == "Running" || value.State == "Publishing")
            && value.LeaseUntilUtc > DateTime.UtcNow, cancellationToken: cancellationToken);

    private Task<List<MediaJob>> PendingAsync(CancellationToken cancellationToken) =>
        Jobs.Find(PendingFilter()).SortBy(value => value.CreatedAtUtc).Limit(32).ToListAsync(cancellationToken);

    private bool HasCompletedOutput(MediaJob job)
    {
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "jobs", job.Id));
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "completed.json", SearchOption.AllDirectories).Any();
    }

    private static FilterDefinition<MediaJob> PendingFilter()
    {
        var now = DateTime.UtcNow;
        return Builders<MediaJob>.Filter.Where(value => value.Kind == "Clip" && !value.CancelRequested
            && (value.State == "Queued" || (value.State == "Running" || value.State == "Publishing")
                && (value.LeaseUntilUtc == null || value.LeaseUntilUtc < now)));
    }

    private static bool IsConfigured(TranscodeWorker worker) => worker.Kind == "Clipping" && worker.Capabilities.ClippingVersion >= 1
        && worker.ConfigurationError is null && worker.AppliedConfigurationRevision == worker.ConfigurationRevision
        && !string.IsNullOrWhiteSpace(worker.Configuration.TempDirectory)
        && worker.Telemetry.FreeBytes >= worker.Configuration.MinimumFreeBytes;

    private static bool CanAssign(TranscodeWorker worker, ClipAssignment assignment, WorkerState? currentState = null)
    {
        long required;
        try
        {
            required = ClipWorkspacePolicy.RequiredBytes(assignment);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            return false;
        }
        if (required > worker.Configuration.MaxCacheBytes || required > worker.Telemetry.FreeBytes - worker.Configuration.MinimumFreeBytes)
        {
            return false;
        }
        var response = new WorkerState
        {
            Configuration = worker.Configuration, ConfigurationRevision = worker.ConfigurationRevision,
            RecoveryResults = currentState?.RecoveryResults ?? [],
            Leases = currentState?.Leases ?? Enumerable.Range(0, 65).Select(_ => new WorkerLeaseStatus
            {
                JobId = new('0', 128), Attempt = int.MaxValue
            }).ToList(),
            ClipAssignments = [assignment with { Attempt = int.MaxValue, LeaseToken = new('0', 64) }]
        };
        var message = new WorkerControlMessage
        {
            Type = "State", RequestId = new('0', 128), Payload = JsonSerializer.SerializeToElement(response, WorkerProtocol.JsonOptions)
        };
        return JsonSerializer.SerializeToUtf8Bytes(message, WorkerProtocol.JsonOptions).Length <= WorkerProtocol.MAX_MESSAGE_BYTES - 1024;
    }
}
