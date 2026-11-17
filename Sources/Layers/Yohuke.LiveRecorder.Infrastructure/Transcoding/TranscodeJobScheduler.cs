using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>幂等规划物理媒体派生任务，并只向持有有效配置的节点授予租约。</summary>
public sealed class TranscodeJobScheduler(IMongoContext context, ISettingsStore settingsStore, IAppPaths paths,
    LibraryService library, TranscodeAssetRegistry assets, TranscodeWorkerService workers, TranscodeLeaseService leases)
{
    /// <summary>读取受范围限制的近期任务。</summary>
    public async Task<IReadOnlyList<TranscodeJob>> ListAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var filter = sessionId is null ? FilterDefinition<TranscodeJob>.Empty : Builders<TranscodeJob>.Filter.Eq(x => x.SessionId, sessionId);
        return await Jobs.Find(filter).SortByDescending(x => x.CreatedAtUtc).Limit(500).ToListAsync(cancellationToken);
    }

    /// <summary>为指定场次的已关闭物理分段建立档位任务，重复调用不会重复转码。</summary>
    public async Task<int> QueueAsync(IReadOnlyCollection<string> sessionIds, IReadOnlyCollection<string>? qualityIds,
        bool force, CancellationToken cancellationToken, bool allowHidden = false, IReadOnlyCollection<string>? assetIds = null,
        TaskProvenance? provenance = null)
    {
        if (sessionIds.Count is 0 or > 200)
        {
            throw new InfrastructureException("一次请选择 1 至 200 个场次。");
        }
        var settings = (await settingsStore.GetAsync(cancellationToken)).Transcoding;
        TranscodingSettingsPolicy.Validate(settings);
        await assets.EnsureIndexesAsync(cancellationToken);
        var profiles = settings.Profiles.Where(x => x.Enabled && (qualityIds is null || qualityIds.Count == 0 || qualityIds.Contains(x.Id))).ToArray();
        if (qualityIds is { Count: > 0 } && qualityIds.Distinct().Any(id => !profiles.Any(x => x.Id == id)))
        {
            throw new InfrastructureException("请求的转码档位不存在或已停用。");
        }
        var count = 0;
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        foreach (var id in sessionIds.Distinct())
        {
            var detail = await library.GetAsync(id, cancellationToken);
            if (detail.Session.EndedAtUtc is null || detail.Session.IsHidden && !allowHidden || detail.Segments.Any(x => !x.IsClosed))
            {
                throw new InfrastructureException("请等待录播结束后再转码。", 409);
            }
            await SessionSegmentTrashFiles.EnsureNoPendingAsync(paths.RecordsDirectory, id, cancellationToken);
            foreach (var segment in detail.Segments.Where(x => x.CommittedBytes > 0 && x.DurationMs > 0
                && (assetIds is null || assetIds.Contains(TranscodeAssetRegistry.AssetId(detail.Session, x)))))
            {
                var asset = await assets.EnsureAsync(detail.Session, segment, cancellationToken);
                foreach (var profile in profiles)
                {
                    var recipe = TranscodeRecipe.Hash(profile);
                    var jobId = TranscodeAssetRegistry.Hash($"{asset.Id}:{asset.SourceVersion}:{profile.Id}:{recipe}");
                    var job = new TranscodeJob
                    {
                        Id = jobId,
                        SessionId = id,
                        Title = detail.Session.Title,
                        AssetId = asset.Id,
                        SourceVersion = asset.SourceVersion,
                        RecipeHash = recipe,
                        Profile = JsonSerializer.Deserialize<TranscodeProfile>(JsonSerializer.Serialize(profile))!,
                        ProfileName = profile.Name,
                        Force = force, Provenance = provenance ?? TaskProvenance.Automatic(),
                        Stages = WorkerStageSnapshotPolicy.Create("Transcoding")
                    };
                    try
                    {
                        await Jobs.InsertOneAsync(job, cancellationToken: cancellationToken);
                        count++;
                    }
                    catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
                    {
                        if (force)
                        {
                            var changed = await Jobs.UpdateOneAsync(x => x.Id == jobId && x.State != "Running" && x.State != "Publishing" && x.State != "Queued",
                                Reset().Set(x => x.Force, true).Set(x => x.SessionId, id).Set(x => x.Title, detail.Session.Title)
                                    .Set(x => x.Provenance, provenance ?? TaskProvenance.Automatic()).Inc(x => x.CheckpointGeneration, 1)
                                    .Set(x => x.ResumeWorkerId, null).Set(x => x.Stages, WorkerStageSnapshotPolicy.Create("Transcoding")),
                                cancellationToken: cancellationToken);
                            count += (int)changed.ModifiedCount;
                        }
                    }
                }
            }
        }
        return count;
    }

    /// <summary>取消排队或执行中的任务，立即撤销其数据传输和发布权。</summary>
    public async Task CancelAsync(string id, CancellationToken cancellationToken)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        await Jobs.UpdateOneAsync(x => x.Id == id && (x.State == "Queued" || x.State == "Running" || x.State == "Publishing"),
            Builders<TranscodeJob>.Update.Set(x => x.CancelRequested, true).Set(x => x.State, "Cancelled")
                .Set(x => x.Stage, "Cancelled").Set(x => x.LeaseUntilUtc, null).Set(x => x.LeaseTokenHash, null)
                .Set(x => x.FinishedAtUtc, DateTime.UtcNow).Set(x => x.ErrorCode, null), cancellationToken: cancellationToken);
    }

    /// <summary>仅在原物理版本仍有效时重试失败或取消的任务。</summary>
    public async Task RetryAsync(string id, CancellationToken cancellationToken)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var job = await Jobs.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("转码任务不存在。", 404);
        try
        {
            await assets.RequireActiveAsync(job.AssetId, job.SourceVersion, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new InfrastructureException("任务源文件已被移除或替换，请按当前录像重新创建转码任务。", 410);
        }
        var changed = await Jobs.UpdateOneAsync(x => x.Id == id && (x.State == "Failed" || x.State == "Cancelled" || x.State == "Skipped"),
            Reset().Set(x => x.ResumeWorkerId, job.WorkerId ?? job.ResumeWorkerId), cancellationToken: cancellationToken);
        if (changed.MatchedCount == 0)
        {
            throw new InfrastructureException("当前转码任务不能重试。", 409);
        }
    }

    /// <summary>续租当前任务并为节点分配至多一个预备任务。</summary>
    public async Task<WorkerState> HeartbeatAsync(string workerId, WorkerHeartbeat heartbeat, CancellationToken cancellationToken,
        string? authenticatedSecretHash = null, Func<bool>? connectionIsCurrent = null)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, workerId, cancellationToken);
        var worker = await workers.RequireAsync(workerId, cancellationToken);
        TranscodeWorkerService.RequireCredential(worker, authenticatedSecretHash);
        TranscodeWorkerService.RequireControlConnection(connectionIsCurrent);
        if (worker.Kind != "Transcoding")
        {
            throw new InfrastructureException("此节点不接受转码任务。", 409);
        }
        var update = Builders<TranscodeWorker>.Update.Set(x => x.LastSeenAtUtc, DateTime.UtcNow)
            .Set(x => x.Telemetry, heartbeat.Telemetry).Set(x => x.AppliedConfigurationRevision, heartbeat.AppliedConfigurationRevision)
            .Set(x => x.ConfigurationError, heartbeat.ConfigurationError);
        if (heartbeat.Capabilities is not null)
        {
            update = update.Set(x => x.Capabilities, heartbeat.Capabilities);
            worker.Capabilities = heartbeat.Capabilities;
        }
        await context.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(x => x.Id == workerId && x.Enabled,
            update, cancellationToken: cancellationToken);
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
                await leases.RequireAsync(workerId, progress.JobId, progress.Attempt, progress.LeaseToken, cancellationToken);
                var stage = TranscodeJobProgressPolicy.NormalizeStage(progress.Stage);
                var progressUpdate = Builders<TranscodeJob>.Update.Set(x => x.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(90))
                        .Set(x => x.Progress, double.IsFinite(progress.Progress) ? Math.Clamp(progress.Progress, 0, 99.9) : 0)
                        .Set(x => x.Stage, stage)
                        .Set(x => x.Activity, TranscodeJobProgressPolicy.NormalizeActivity(stage, progress.Activity))
                        .Set(x => x.LastProgressAtUtc, DateTime.UtcNow)
                        .Set(x => x.Encoder, progress.Encoder);
                var stages = WorkerStageSnapshotPolicy.Normalize("Transcoding", progress.Stages);
                if (stages.Count > 0)
                {
                    progressUpdate = progressUpdate.Set(x => x.Stages, stages);
                }
                var changed = await Jobs.UpdateOneAsync(TranscodeLeaseService.Fence(workerId, progress.JobId, progress.Attempt, progress.LeaseToken),
                    progressUpdate, cancellationToken: cancellationToken);
                accepted = changed.MatchedCount > 0;
            }
            catch (InfrastructureException exception) when (exception.StatusCode is 409 or 410)
            {
                accepted = await leases.IsFinalizingAsync(workerId, progress.JobId, progress.Attempt, progress.LeaseToken, cancellationToken);
            }
            state.Leases.Add(new() { JobId = progress.JobId, Attempt = progress.Attempt, Accepted = accepted });
        }
        var settings = (await settingsStore.GetAsync(cancellationToken)).Transcoding;
        if (!settings.Enabled || heartbeat.ConfigurationError is not null || heartbeat.AppliedConfigurationRevision != worker.ConfigurationRevision
            || string.IsNullOrWhiteSpace(worker.Configuration.TempDirectory)
            || heartbeat.Telemetry.FreeBytes < worker.Configuration.MinimumFreeBytes
            || !worker.Capabilities.Encoders.Any(x => x.Available))
        {
            return state;
        }
        var active = await Jobs.CountDocumentsAsync(x => x.WorkerId == workerId && (x.State == "Running" || x.State == "Publishing")
            && x.LeaseUntilUtc > DateTime.UtcNow, cancellationToken: cancellationToken);
        if (active >= worker.Configuration.MaxConcurrentEncodes + 1)
        {
            return state;
        }
        var preparing = await Jobs.CountDocumentsAsync(x => x.WorkerId == workerId && x.State == "Running"
            && x.Stage != "Encoding" && x.Stage != "Uploading" && x.Stage != "Validating" && x.Stage != "Finalizing" && x.LeaseUntilUtc > DateTime.UtcNow,
            cancellationToken: cancellationToken);
        if (preparing > 0)
        {
            return state;
        }
        var expiredBefore = DateTime.UtcNow;
        var filter = Builders<TranscodeJob>.Filter;
        var pending = filter.Where(x => !x.CancelRequested && (x.State == "Queued"
            || ((x.State == "Running" || x.State == "Publishing") && (x.LeaseUntilUtc == null || x.LeaseUntilUtc < expiredBefore))));
        var preferred = await PreferredWorkersWithCapacityAsync(workerId, cancellationToken);
        foreach (var candidate in preferred)
        {
            var reserved = filter.Eq(value => value.ResumeWorkerId, candidate.Id);
            if (candidate.Capabilities.HlsPackagingVersion < 1)
            {
                reserved &= DefaultPackagingFilter();
            }
            pending &= filter.Not(reserved);
        }
        var defaultPackaging = DefaultPackagingFilter();
        if (worker.Capabilities.HlsPackagingVersion < 1)
        {
            pending &= defaultPackaging;
        }
        var gpu = worker.Configuration.GpuConcurrency > 0 && worker.Capabilities.Encoders.Any(x => x.Available && x.Encoder != "libx264"
            && (worker.Configuration.PreferredEncoder == "auto" || worker.Configuration.PreferredEncoder == x.Encoder)
            && (worker.Configuration.DeviceId is null || worker.Configuration.DeviceId == x.DeviceId));
        if (!gpu)
        {
            if (worker.Configuration.CpuFallbackMode == "Disabled" || worker.Configuration.CpuConcurrency <= 0
                || heartbeat.Telemetry.CpuThreadBudget < 1
                || heartbeat.Telemetry.CpuUnavailableReason is not null || heartbeat.Telemetry.CpuPercent is null
                || heartbeat.Telemetry.CpuPercent >= worker.Configuration.CpuLoadThreshold)
            {
                return state;
            }
            var recent = DateTime.UtcNow.AddSeconds(-10);
            var candidates = await context.Collection<TranscodeWorker>("transcodeWorkers")
                .Find(x => x.Enabled && x.Id != workerId && x.LastSeenAtUtc > recent && x.ConfigurationError == null).ToListAsync(cancellationToken);
            var availableGpuWorkers = candidates.Where(x => x.Kind == "Transcoding" && x.AppliedConfigurationRevision == x.ConfigurationRevision
                && x.Telemetry.FreeBytes >= x.Configuration.MinimumFreeBytes
                && x.Telemetry.RunningGpu + x.Telemetry.RunningCpu < x.Configuration.MaxConcurrentEncodes
                && x.Telemetry.RunningGpu < x.Configuration.GpuConcurrency
                && x.Capabilities.Encoders.Any(encoder => encoder.Available && encoder.Encoder != "libx264"
                    && (x.Configuration.PreferredEncoder == "auto" || x.Configuration.PreferredEncoder == encoder.Encoder)
                    && (x.Configuration.DeviceId is null || x.Configuration.DeviceId == encoder.DeviceId))).ToArray();
            if (availableGpuWorkers.Length > 0)
            {
                if (worker.Capabilities.HlsPackagingVersion < 1 || availableGpuWorkers.Any(x => x.Capabilities.HlsPackagingVersion >= 1))
                {
                    return state;
                }
                pending &= filter.Not(defaultPackaging);
            }
        }
        var now = DateTime.UtcNow;
        var token = TranscodeWorkerService.NewSecret();
        var updateLease = Builders<TranscodeJob>.Update.Set(x => x.State, "Running").Set(x => x.Stage, "Downloading")
            .Set(x => x.Progress, 0).Set(x => x.Activity, null).Set(x => x.LastProgressAtUtc, null)
            .Set(x => x.Encoder, null)
            .Set(x => x.WorkerId, workerId).Set(x => x.LeaseTokenHash, TranscodeWorkerService.SecretHash(token))
            .Set(x => x.ResumeWorkerId, workerId)
            .Set(x => x.LeaseUntilUtc, now.AddSeconds(90)).Inc(x => x.Attempt, 1);
        var job = await ClaimByPriorityAsync(settings, pending, updateLease, workerId, cancellationToken);
        if (job is null)
        {
            return state;
        }
        try
        {
            var asset = await assets.RequireActiveAsync(job.AssetId, job.SourceVersion, cancellationToken);
            state.Assignments.Add(new()
            {
                JobId = job.Id, Attempt = job.Attempt, CheckpointGeneration = job.CheckpointGeneration, LeaseToken = token,
                AssetId = asset.Id, SourceVersion = asset.SourceVersion, RecipeHash = job.RecipeHash,
                SourceLength = asset.CommittedBytes, SourceDurationMs = asset.DurationMs,
                SourceETag = "\"" + asset.SourceVersion + "\"", SourceFileName = Path.GetFileName(asset.SourceRelativePath), Profile = job.Profile
            });
        }
        catch (InvalidOperationException)
        {
            await Jobs.UpdateOneAsync(x => x.Id == job.Id && x.Attempt == job.Attempt,
                Builders<TranscodeJob>.Update.Set(x => x.State, "Cancelled").Set(x => x.Stage, "SourceUnavailable")
                    .Set(x => x.ErrorCode, "SourceUnavailable").Set(x => x.LeaseUntilUtc, null).Set(x => x.LeaseTokenHash, null), cancellationToken: cancellationToken);
        }
        return state;
    }

    private IMongoCollection<TranscodeJob> Jobs => context.Collection<TranscodeJob>("transcodeJobs");

    private async Task<TranscodeJob?> ClaimByPriorityAsync(TranscodingSettings settings, FilterDefinition<TranscodeJob> pending,
        UpdateDefinition<TranscodeJob> update, string workerId, CancellationToken cancellationToken)
    {
        var groups = settings.Profiles.GroupBy(x => x.Priority)
            .ToDictionary(group => group.Key, group => group.Select(x => x.Id).ToArray());
        var prioritizedIds = groups.Where(group => group.Key != 0).SelectMany(group => group.Value).ToArray();
        var options = new FindOneAndUpdateOptions<TranscodeJob>
        {
            ReturnDocument = ReturnDocument.After,
            Sort = Builders<TranscodeJob>.Sort.Ascending(x => x.CreatedAtUtc).Ascending(x => x.Id)
        };
        foreach (var priority in groups.Keys.Append(0).Distinct().OrderDescending())
        {
            var profileFilter = priority == 0
                ? Builders<TranscodeJob>.Filter.Nin(x => x.Profile.Id, prioritizedIds)
                : Builders<TranscodeJob>.Filter.In(x => x.Profile.Id, groups[priority]);
            var job = await Jobs.FindOneAndUpdateAsync(pending & profileFilter & Builders<TranscodeJob>.Filter.Eq(value => value.ResumeWorkerId, workerId),
                update, options, cancellationToken)
                ?? await Jobs.FindOneAndUpdateAsync(pending & profileFilter, update, options, cancellationToken);
            if (job is not null)
            {
                return job;
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<TranscodeWorker>> PreferredWorkersWithCapacityAsync(string currentWorkerId, CancellationToken cancellationToken)
    {
        var recent = DateTime.UtcNow.AddSeconds(-10);
        var candidates = await context.Collection<TranscodeWorker>("transcodeWorkers")
            .Find(value => value.Id != currentWorkerId && value.Enabled && value.Kind == "Transcoding" && value.LastSeenAtUtc > recent
                && value.ConfigurationError == null && value.AppliedConfigurationRevision == value.ConfigurationRevision).ToListAsync(cancellationToken);
        var available = new List<TranscodeWorker>();
        foreach (var candidate in candidates)
        {
            var configuration = candidate.Configuration;
            var gpu = configuration.GpuConcurrency > 0 && candidate.Telemetry.RunningGpu < configuration.GpuConcurrency
                && candidate.Capabilities.Encoders.Any(encoder => encoder.Available && encoder.Encoder != "libx264"
                    && (configuration.PreferredEncoder == "auto" || configuration.PreferredEncoder == encoder.Encoder)
                    && (configuration.DeviceId == null || configuration.DeviceId == encoder.DeviceId));
            var cpu = configuration.CpuFallbackMode != "Disabled" && configuration.CpuConcurrency > candidate.Telemetry.RunningCpu
                && candidate.Telemetry.CpuThreadBudget > 0 && candidate.Telemetry.CpuUnavailableReason == null
                && candidate.Telemetry.CpuPercent < configuration.CpuLoadThreshold
                && candidate.Capabilities.Encoders.Any(encoder => encoder.Available && encoder.Encoder == "libx264");
            if (string.IsNullOrWhiteSpace(configuration.TempDirectory) || candidate.Telemetry.FreeBytes < configuration.MinimumFreeBytes
                || !gpu && !cpu || candidate.Telemetry.RunningGpu + candidate.Telemetry.RunningCpu >= configuration.MaxConcurrentEncodes)
            {
                continue;
            }
            var active = await Jobs.CountDocumentsAsync(job => job.WorkerId == candidate.Id && (job.State == "Running" || job.State == "Publishing")
                && job.LeaseUntilUtc > DateTime.UtcNow, cancellationToken: cancellationToken);
            var preparing = await Jobs.CountDocumentsAsync(job => job.WorkerId == candidate.Id && job.State == "Running"
                && job.Stage != "Encoding" && job.Stage != "Uploading" && job.Stage != "Validating" && job.Stage != "Finalizing"
                && job.LeaseUntilUtc > DateTime.UtcNow, cancellationToken: cancellationToken);
            if (active < configuration.MaxConcurrentEncodes + 1 && preparing == 0)
            {
                available.Add(candidate);
            }
        }
        return available;
    }

    private static FilterDefinition<TranscodeJob> DefaultPackagingFilter()
    {
        var filter = Builders<TranscodeJob>.Filter;
        return (filter.Eq(x => x.Profile.SegmentDurationSeconds, 6) | filter.Exists("Profile.SegmentDurationSeconds", false))
            & (filter.Eq(x => x.Profile.KeyframeIntervalSeconds, 2) | filter.Exists("Profile.KeyframeIntervalSeconds", false))
            & (filter.Eq(x => x.Profile.SingleFile, false) | filter.Exists("Profile.SingleFile", false));
    }

    internal static UpdateDefinition<TranscodeJob> Reset() => Builders<TranscodeJob>.Update.Set(x => x.State, "Queued")
        .Set(x => x.Stage, "WaitingForWorker").Set(x => x.Progress, 0).Set(x => x.CancelRequested, false)
        .Set(x => x.Activity, null).Set(x => x.LastProgressAtUtc, null)
        .Set(x => x.Error, null).Set(x => x.ErrorCode, null).Set(x => x.WorkerId, null).Set(x => x.Encoder, null)
        .Set(x => x.PublicationReady, false)
        .Set(x => x.LeaseUntilUtc, null).Set(x => x.LeaseTokenHash, null).Set(x => x.FinishedAtUtc, null);
}
