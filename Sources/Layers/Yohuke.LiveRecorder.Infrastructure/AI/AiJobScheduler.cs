using MongoDB.Driver;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>在主站全局与节点容量约束下派发 AI 执行，未知模型请求不会自动重排。</summary>
public sealed class AiJobScheduler(IMongoContext context, ISettingsStore settingsStore, IAppPaths paths,
    TranscodeWorkerService workers, AiJobExecutionService execution, AiConnectionTestCoordinator probes)
{
    private IMongoCollection<AiAnalysisJob> Jobs => context.Collection<AiAnalysisJob>("ai_analysis_jobs");

    /// <summary>校验控制连接、续租当前任务并领取空闲容量内的 AI 作业。</summary>
    public async Task<WorkerState> HeartbeatAsync(string workerId, WorkerHeartbeat heartbeat, CancellationToken cancellationToken,
        string? authenticatedSecretHash = null, Func<bool>? connectionIsCurrent = null)
    {
        await using var identity = await WorkerIdentityLock.AcquireAsync(paths, workerId, cancellationToken);
        var worker = await workers.RequireAsync(workerId, cancellationToken);
        TranscodeWorkerService.RequireCredential(worker, authenticatedSecretHash);
        TranscodeWorkerService.RequireControlConnection(connectionIsCurrent);
        if (worker.Kind != "AI")
        {
            throw AiErrors.CreateException("AI_LEASE_LOST", 409);
        }
        if (heartbeat.Capabilities is not null)
        {
            worker.Capabilities = heartbeat.Capabilities;
        }
        await context.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == workerId,
            Builders<TranscodeWorker>.Update.Set(value => value.LastSeenAtUtc, DateTime.UtcNow)
                .Set(value => value.Telemetry, heartbeat.Telemetry).Set(value => value.Capabilities, worker.Capabilities)
                .Set(value => value.AppliedConfigurationRevision, heartbeat.AppliedConfigurationRevision)
                .Set(value => value.ConfigurationError, heartbeat.ConfigurationError), cancellationToken: cancellationToken);
        var state = new WorkerState { Configuration = worker.Configuration, ConfigurationRevision = worker.ConfigurationRevision };
        await using var scheduling = await SchedulingLockAsync(cancellationToken);
        foreach (var recovery in heartbeat.RecoveryRequests.Take(64))
        {
            var local = heartbeat.AiRecoveries.FirstOrDefault(value => value.JobId == recovery.JobId && value.Attempt == recovery.Attempt);
            state.RecoveryResults.Add(await execution.RecoverAsync(workerId, recovery, local, cancellationToken));
        }
        foreach (var progress in heartbeat.Jobs.Take(64))
        {
            state.Leases.Add(new()
            {
                JobId = progress.JobId, Attempt = progress.Attempt,
                Accepted = await execution.RenewAsync(workerId, progress, cancellationToken)
            });
        }
        await execution.MaintainAsync(cancellationToken);
        if (heartbeat.ConfigurationError is not null || heartbeat.AppliedConfigurationRevision != worker.ConfigurationRevision
            || worker.Capabilities.AiExecutionVersion < 1 || string.IsNullOrWhiteSpace(worker.Configuration.TempDirectory)
            || heartbeat.Telemetry.FreeBytes < worker.Configuration.MinimumFreeBytes)
        {
            return state;
        }
        var settings = (await settingsStore.GetAsync(cancellationToken)).Ai;
        var now = DateTime.UtcNow;
        var global = await Jobs.CountDocumentsAsync(value => value.State == "Running" && value.LeaseUntilUtc > now,
            cancellationToken: cancellationToken) + probes.RunningCount();
        var localCount = await Jobs.CountDocumentsAsync(value => value.WorkerId == workerId && value.State == "Running"
            && value.LeaseUntilUtc > now, cancellationToken: cancellationToken) + probes.RunningCount(workerId);
        var available = Math.Min(settings.MaxConcurrentJobs - global, worker.Configuration.MaxConcurrentEncodes - localCount);
        for (var index = 0; index < available; index++)
        {
            TranscodeWorkerService.RequireControlConnection(connectionIsCurrent);
            var probe = probes.Assign(workerId);
            if (probe is not null)
            {
                state.AiAssignments.Add(probe);
                continue;
            }
            if (!settings.Enabled)
            {
                break;
            }
            var token = TranscodeWorkerService.NewSecret();
            var job = await Jobs.FindOneAndUpdateAsync(value => value.State == "Queued" && !value.CancelRequested,
                Builders<AiAnalysisJob>.Update.Set(value => value.State, "Running").Set(value => value.WorkerId, workerId)
                    .Set(value => value.LeaseOwner, workerId).Set(value => value.LeaseUntilUtc, now.AddSeconds(90))
                    .Set(value => value.LeaseTokenHash, TranscodeWorkerService.SecretHash(token))
                    .Set(value => value.ReportOnlyRecovery, false).Set(value => value.CheckpointSequence, 0)
                    .Set(value => value.CheckpointHash, null).Inc(value => value.Attempt, 1),
                new FindOneAndUpdateOptions<AiAnalysisJob>
                {
                    ReturnDocument = ReturnDocument.After, Sort = Builders<AiAnalysisJob>.Sort.Ascending(value => value.CreatedAtUtc)
                }, cancellationToken);
            if (job is null)
            {
                break;
            }
            state.AiAssignments.Add(new()
            {
                JobId = job.Id, Attempt = job.Attempt, LeaseToken = token,
                InputFingerprint = job.Input?.Fingerprint ?? job.Snapshot.SourceFingerprint,
                ConfigurationFingerprint = AiConnectionTestCoordinator.ConfigurationFingerprint(job.Settings)
            });
        }
        return state;
    }

    /// <summary>交互式测试只在存在在线节点和可用容量时等待一次结果。</summary>
    public async Task<AiConnectionTestResult> TestConnectionAsync(AiSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        AiConnectionProbe probe;
        await using (await SchedulingLockAsync(cancellationToken))
        {
            var current = (await settingsStore.GetAsync(cancellationToken)).Ai;
            var now = DateTime.UtcNow;
            var candidates = await context.Collection<TranscodeWorker>("transcodeWorkers")
                .Find(value => value.Kind == "AI" && value.Enabled && value.LastSeenAtUtc > now.AddSeconds(-30)
                    && value.Capabilities.AiExecutionVersion >= 1 && value.AppliedConfigurationRevision == value.ConfigurationRevision
                    && value.ConfigurationError == null).ToListAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                throw AiErrors.CreateException("AI_WORKER_UNAVAILABLE", 503);
            }
            var active = await Jobs.CountDocumentsAsync(value => value.State == "Running" && value.LeaseUntilUtc > now,
                cancellationToken: cancellationToken);
            var capacity = false;
            foreach (var candidate in candidates)
            {
                var running = await Jobs.CountDocumentsAsync(value => value.WorkerId == candidate.Id && value.State == "Running"
                    && value.LeaseUntilUtc > now, cancellationToken: cancellationToken);
                capacity |= running + probes.RunningCount(candidate.Id) < candidate.Configuration.MaxConcurrentEncodes;
            }
            if (!capacity || active + probes.ActiveCount >= current.MaxConcurrentJobs)
            {
                throw AiErrors.CreateException("AI_WORKER_BUSY", 409);
            }
            probe = probes.Enqueue(AiExecutionMapper.ToSettings(AiExecutionMapper.ToSettings(settings)), apiKey);
        }
        try
        {
            return await probe.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            probes.Release(probe);
        }
    }

    private Task<FileStream> SchedulingLockAsync(CancellationToken cancellationToken) =>
        MediaJobExecutionLock.AcquireAsync(paths, "ai-scheduling", cancellationToken);
}
