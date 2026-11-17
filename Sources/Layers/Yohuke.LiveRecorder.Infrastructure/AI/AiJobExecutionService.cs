using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>以持久执行身份保存 AI 检查点，模型调用始终由独立节点承担。</summary>
public sealed class AiJobExecutionService(IMongoContext context, ISettingsStore settingsStore, IAppPaths paths,
    TranscodeWorkerService workers, LibraryService library, ISecretProtector protector, IStatusPublisher publisher,
    AiLiveMarkerPublisher liveMarkers, AiInputManifestFactory manifests, AiConnectionTestCoordinator probes)
{
    private IMongoCollection<AiAnalysisJob> Jobs => context.Collection<AiAnalysisJob>("ai_analysis_jobs");

    /// <summary>在有效租约下领取不含主站文件路径的模型执行参数。</summary>
    public async Task<AiExecutionSpecification> GetExecutionAsync(string workerId, string jobId, int attempt, string token, CancellationToken cancellationToken)
    {
        await RequireWorkerAsync(workerId, cancellationToken);
        if (probes.Contains(jobId))
        {
            return probes.Specification(workerId, jobId, attempt, token);
        }
        await using var jobLock = await LockAsync(jobId, cancellationToken);
        var job = await RequireAsync(workerId, jobId, attempt, token, cancellationToken);
        if (job.Input is null)
        {
            job.Input = manifests.Legacy(job);
            job.SourcePaths = [job.Snapshot.EvidenceRelativePath];
            await Jobs.UpdateOneAsync(Fence(job), Builders<AiAnalysisJob>.Update.Set(value => value.Input, job.Input)
                .Set(value => value.SourcePaths, job.SourcePaths), cancellationToken: cancellationToken);
        }
        var settings = (await settingsStore.GetAsync(cancellationToken)).Ai;
        AiAnalysisMemory? memory = null;
        if (job.Context.ConversationPath is { Length: > 0 } relative)
        {
            await using var input = File.OpenRead(MediaPaths.Resolve(paths.DataDirectory, relative));
            memory = await JsonSerializer.DeserializeAsync<AiAnalysisMemory>(input, JsonFiles.Options, cancellationToken);
        }
        return new()
        {
            Execution = AiExecutionMapper.ToExecution(job), Input = job.Input, Memory = memory, CheckpointSequence = job.CheckpointSequence,
            ApiKey = string.IsNullOrEmpty(settings.ApiKey) ? "" : protector.Unprotect(settings.ApiKey)
        };
    }

    /// <summary>只授权清单中固定版本和长度的来源，不接收节点提供的文件路径。</summary>
    public async Task<AiSourceAsset> GetSourceAsync(string workerId, string jobId, int attempt, string token, string sourceId,
        CancellationToken cancellationToken)
    {
        await using var jobLock = await LockAsync(jobId, cancellationToken);
        var job = await RequireAsync(workerId, jobId, attempt, token, cancellationToken);
        if (job.Input is null || !int.TryParse(sourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0 || index >= job.Input.Sources.Count || index >= job.SourcePaths.Count)
        {
            throw AiErrors.CreateException("AI_SOURCE_UNAVAILABLE", 410);
        }
        var source = job.Input.Sources[index];
        var path = MediaPaths.Resolve(job.Input.PreparedSnapshot ? paths.DataDirectory : paths.RecordsDirectory, job.SourcePaths[index]);
        var file = new FileInfo(path);
        if (source.Id != sourceId || !file.Exists || file.Length < source.Length || file.LastWriteTimeUtc.Ticks != source.LastWriteUtcTicks)
        {
            throw AiErrors.CreateException("AI_SOURCE_UNAVAILABLE", 410);
        }
        return new(path, source.Length);
    }

    /// <summary>严格按序幂等确认进度，只有持有当前执行代次的节点可以推进检查点。</summary>
    public async Task<AiCheckpointAcknowledgement> CheckpointAsync(string workerId, string jobId, int attempt, string token,
        AiExecutionCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await RequireWorkerAsync(workerId, cancellationToken);
        if (probes.Contains(jobId))
        {
            return probes.Checkpoint(workerId, jobId, attempt, token, checkpoint);
        }
        await using var jobLock = await LockAsync(jobId, cancellationToken);
        var job = await RequireAsync(workerId, jobId, attempt, token, cancellationToken);
        var hash = Hash(checkpoint);
        if (Repeated(job, checkpoint.Sequence, hash))
        {
            return new() { Sequence = job.CheckpointSequence, State = job.ReportOnlyRecovery ? "ReportOnly" : job.State };
        }
        Validate(job, checkpoint.Execution, checkpoint.Sequence);
        Apply(job, checkpoint.Execution);
        await SaveMemoryAsync(job, checkpoint.Memory, checkpoint.Sequence, cancellationToken);
        job.CheckpointSequence = checkpoint.Sequence;
        job.CheckpointHash = hash;
        await PersistAsync(job, cancellationToken);
        return new() { Sequence = job.CheckpointSequence, State = job.ReportOnlyRecovery ? "ReportOnly" : job.State };
    }

    /// <summary>校验终态与证据身份，保存结果后由主站幂等发布直播记录。</summary>
    public async Task<AiCheckpointAcknowledgement> CompleteAsync(string workerId, string jobId, int attempt, string token,
        AiExecutionResult result, CancellationToken cancellationToken)
    {
        await RequireWorkerAsync(workerId, cancellationToken);
        if (probes.Contains(jobId))
        {
            return probes.Complete(workerId, jobId, attempt, token, result);
        }
        await using var jobLock = await LockAsync(jobId, cancellationToken);
        var job = await Jobs.Find(value => value.Id == jobId).FirstOrDefaultAsync(cancellationToken)
            ?? throw AiErrors.CreateException("AI_SOURCE_UNAVAILABLE", 410);
        RequireIdentity(job, workerId, attempt, token);
        var hash = Hash(result);
        if (Repeated(job, result.Sequence, hash))
        {
            await PublishPendingAsync(job, cancellationToken);
            return new() { Sequence = job.CheckpointSequence, State = job.State };
        }
        job = await RequireAsync(workerId, jobId, attempt, token, cancellationToken);
        Validate(job, result.Execution, result.Sequence);
        if (result.Execution.State is not ("Completed" or "Partial" or "Failed" or "Interrupted" or "Cancelled"))
        {
            throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
        }
        Apply(job, result.Execution);
        if (result.Stages.Count > 0)
        {
            var stages = result.Stages;
            if (stages.Count > 4 || stages.Select(value => value.Id).Distinct().Count() != stages.Count
                || stages.Any(value => value.Id is not ("Downloading" or "Preparing" or "Analyzing" or "Reporting")
                    || !double.IsFinite(value.Progress) || value.Progress is < 0 or > 100
                    || !double.IsFinite(value.ElapsedMs) || value.ElapsedMs < 0))
            {
                throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
            }
            job.Stages = stages.Select(value => new WorkerStageSnapshot
            {
                Id = value.Id, State = value.State, Progress = value.Progress,
                StartedAtUtc = value.StartedAtUtc, FinishedAtUtc = value.FinishedAtUtc,
                ElapsedMs = value.ElapsedMs, ErrorCode = value.ErrorCode
            }).ToList();
        }
        await SaveMemoryAsync(job, result.Memory, result.Sequence, cancellationToken);
        job.CheckpointSequence = result.Sequence;
        job.CheckpointHash = hash;
        job.State = result.Execution.State;
        if (job.RequestInFlight)
        {
            job.State = "Interrupted";
            job.ErrorCode ??= "AI_REQUEST_UNKNOWN";
            job.UnsettledModelRequest = true;
            job.UnsettledReservedTokens = job.ReservedTokens;
            job.EstimatedTokens += job.ReservedTokens;
            job.ReservedTokens = 0;
            job.RequestInFlight = false;
        }
        if (job.State == "Completed" && (!job.Result.IsComplete || job.Context.Segments.Count == 0
            || job.Context.Segments.Any(value => value.State is not ("Completed" or "ReviewedNoTopics" or "Empty"))))
        {
            throw AiErrors.CreateException("AI_LIVE_MARKER_RESULT_INVALID", 409);
        }
        if (job.State != "Completed")
        {
            job.Result.IsComplete = false;
            job.Result.CompletedAtUtc = null;
        }
        if (job.State == "Completed" && job.Purpose == AiJobPurposes.LIVE_MARKERS)
        {
            job.PublicationStarted = true;
            job.Context.Phase = "Publishing";
            job.State = "Running";
        }
        else
        {
            Finalize(job);
        }
        await PersistAsync(job, cancellationToken);
        await PublishPendingAsync(job, cancellationToken);
        return new() { Sequence = job.CheckpointSequence, State = job.State };
    }

    /// <summary>只续租匹配执行身份且仍允许执行的任务。</summary>
    public async Task<bool> RenewAsync(string workerId, WorkerJobProgress progress, CancellationToken cancellationToken)
    {
        if (probes.Contains(progress.JobId))
        {
            return probes.Renew(workerId, progress.JobId, progress.Attempt, progress.LeaseToken);
        }
        await using var jobLock = await LockAsync(progress.JobId, cancellationToken);
        try
        {
            var job = await RequireAsync(workerId, progress.JobId, progress.Attempt, progress.LeaseToken, cancellationToken);
            if (job.PublicationStarted)
            {
                return true;
            }
            job.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(90);
            if (progress.Stages.Count > 0)
            {
                job.Stages = progress.Stages;
            }
            await PersistAsync(job, cancellationToken);
            return true;
        }
        catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
        {
            return false;
        }
    }

    /// <summary>使用旧租约证明恢复同一代次，已知响应仅取得回传权限。</summary>
    public async Task<WorkerJobRecoveryStatus> RecoverAsync(string workerId, WorkerJobRecoveryRequest request,
        AiRecoveryCheckpoint? local, CancellationToken cancellationToken)
    {
        await using var jobLock = await LockAsync(request.JobId, cancellationToken);
        var rejected = new WorkerJobRecoveryStatus { JobId = request.JobId, Attempt = request.Attempt };
        var job = await Jobs.Find(value => value.Id == request.JobId).FirstOrDefaultAsync(cancellationToken);
        if (job is null || job.WorkerId != workerId || job.Attempt != request.Attempt || job.CancelRequested
            || request.NextLeaseToken.Length is < 16 or > 256
            || job.LeaseTokenHash != TranscodeWorkerService.SecretHash(request.LeaseToken)
                && job.LeaseTokenHash != TranscodeWorkerService.SecretHash(request.NextLeaseToken))
        {
            return rejected;
        }
        if (job.State is "Completed" or "Failed" or "Cancelled" or "Partial")
        {
            return rejected with { Disposition = "Completed", State = job.State };
        }
        if (job.State is not ("Running" or "Interrupted"))
        {
            return rejected;
        }
        var activeKey = job.Purpose == AiJobPurposes.ANALYSIS ? job.SessionId : job.Purpose + ":" + job.SessionId;
        if (await Jobs.Find(value => value.Id != job.Id && value.ActiveKey == activeKey).AnyAsync(cancellationToken))
        {
            return rejected;
        }
        var sameRequest = local is not null && local.ModelRequests == job.ModelRequests && local.Sequence >= job.CheckpointSequence;
        var unsettled = job.RequestInFlight || job.UnsettledModelRequest;
        var unsent = unsettled && sameRequest && !local!.HasDispatchedRequest;
        var known = sameRequest && local!.HasKnownResponse;
        if (unsettled && !known && !unsent)
        {
            await InterruptAsync(job, "AI_REQUEST_UNKNOWN", cancellationToken);
            return rejected with { State = "Interrupted" };
        }
        var reportOnly = !unsent && (unsettled || job.State == "Interrupted");
        if (unsent)
        {
            job.RequestInFlight = false;
            job.ReservedTokens = 0;
            job.ModelRequests = Math.Max(0, job.ModelRequests - 1);
            job.EstimatedTokens = Math.Max(0, job.EstimatedTokens - job.UnsettledReservedTokens);
            job.UnsettledModelRequest = false;
            job.UnsettledReservedTokens = 0;
        }
        job.State = "Running";
        job.ActiveKey = activeKey;
        job.LeaseTokenHash = TranscodeWorkerService.SecretHash(request.NextLeaseToken);
        job.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(90);
        job.ReportOnlyRecovery = reportOnly;
        try
        {
            await PersistAsync(job, cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return rejected;
        }
        return rejected with { Disposition = reportOnly ? "ReportOnly" : "Resume", State = job.State };
    }

    /// <summary>恢复发布并停止过期或撤销的任务；未知请求从不重新排队。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken)
    {
        var pending = await Jobs.Find(value => value.State == "Running"
            && (value.PublicationStarted || value.CancelRequested || value.LeaseUntilUtc == null || value.LeaseUntilUtc < DateTime.UtcNow))
            .Limit(100).ToListAsync(cancellationToken);
        foreach (var candidate in pending)
        {
            await using var jobLock = await LockAsync(candidate.Id, cancellationToken);
            var job = await Jobs.Find(value => value.Id == candidate.Id && value.State == "Running").FirstOrDefaultAsync(cancellationToken);
            if (job is null)
            {
                continue;
            }
            if (job.CancelRequested && !job.PublicationStarted)
            {
                job.State = "Cancelled";
                job.ErrorCode = "AI_CANCELLED";
                job.Result.IsComplete = false;
                job.Result.CompletedAtUtc = null;
                Finalize(job);
                await PersistAsync(job, cancellationToken);
                continue;
            }
            if (job.PublicationStarted || job.Result.IsComplete && !job.RequestInFlight
                && job.Context.Phase is "Completed" or "Publishing" && job.Context.Segments.Count > 0
                && job.Context.Segments.All(value => value.State is "Completed" or "ReviewedNoTopics" or "Empty"))
            {
                if (job.Purpose == AiJobPurposes.LIVE_MARKERS)
                {
                    job.PublicationStarted = true;
                    await PersistAsync(job, cancellationToken);
                    await PublishPendingAsync(job, cancellationToken);
                }
                else
                {
                    job.State = "Completed";
                    Finalize(job);
                    await PersistAsync(job, cancellationToken);
                }
                continue;
            }
            if (job.CancelRequested)
            {
                job.State = "Cancelled";
                job.ErrorCode = "AI_CANCELLED";
                Finalize(job);
                await PersistAsync(job, cancellationToken);
            }
            else if (job.LeaseUntilUtc is null || job.LeaseUntilUtc < DateTime.UtcNow)
            {
                if (job.ModelRequests == 0 && !job.RequestInFlight && !job.UnsettledModelRequest && job.WorkerId is not null)
                {
                    job.State = "Queued";
                    job.WorkerId = null;
                    job.LeaseTokenHash = null;
                    job.LeaseUntilUtc = null;
                    await PersistAsync(job, cancellationToken);
                }
                else
                {
                    await InterruptAsync(job, "AI_PROCESS_INTERRUPTED", cancellationToken);
                }
            }
        }
    }

    internal async Task<AiAnalysisJob> RequireAsync(string workerId, string jobId, int attempt, string token,
        CancellationToken cancellationToken)
    {
        await RequireWorkerAsync(workerId, cancellationToken);
        var job = await Jobs.Find(value => value.Id == jobId).FirstOrDefaultAsync(cancellationToken)
            ?? throw AiErrors.CreateException("AI_SOURCE_UNAVAILABLE", 410);
        RequireIdentity(job, workerId, attempt, token);
        if (job.State != "Running" || job.LeaseUntilUtc is null || job.LeaseUntilUtc <= DateTime.UtcNow || job.CancelRequested && !job.PublicationStarted)
        {
            throw AiErrors.CreateException("AI_LEASE_LOST", 409);
        }
        var configuration = await settingsStore.GetAsync(cancellationToken);
        var settings = configuration.Ai;
        var code = !settings.Enabled ? "AI_DISABLED"
            : AiConnectionTestCoordinator.ConfigurationFingerprint(settings) != AiConnectionTestCoordinator.ConfigurationFingerprint(job.Settings)
                || AiSettingsSecrets.Fingerprint(settings) != job.CredentialFingerprint ? "AI_CONFIGURATION_CHANGED" : null;
        if (code is not null && !job.PublicationStarted)
        {
            await InterruptAsync(job, code, cancellationToken);
            throw AiErrors.CreateException(code, 409);
        }
        SessionDetail detail;
        try
        {
            detail = await library.GetAsync(job.SessionId, cancellationToken);
        }
        catch (InfrastructureException error) when (error.StatusCode is 404 or 410)
        {
            await InterruptAsync(job, "AI_SOURCE_UNAVAILABLE", cancellationToken);
            throw AiErrors.CreateException("AI_SOURCE_UNAVAILABLE", 410);
        }
        if (detail.Session.IsHidden || detail.Session.IsTrashed || !detail.Session.ProjectionReady || detail.Session.EndedAtUtc is null)
        {
            await InterruptAsync(job, "AI_SOURCE_UNAVAILABLE", cancellationToken);
            throw AiErrors.CreateException("AI_SOURCE_UNAVAILABLE", 410);
        }
        if (job.Input is { PreparedSnapshot: false } input && manifests.Fingerprint(detail, configuration) != input.Fingerprint)
        {
            await InterruptAsync(job, "AI_SOURCE_UNAVAILABLE", cancellationToken);
            throw AiErrors.CreateException("AI_SOURCE_UNAVAILABLE", 410);
        }
        return job;
    }

    private async Task RequireWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        var worker = await workers.RequireAsync(workerId, cancellationToken);
        if (worker.Kind != "AI")
        {
            throw AiErrors.CreateException("AI_LEASE_LOST", 409);
        }
    }

    private static void RequireIdentity(AiAnalysisJob job, string workerId, int attempt, string token)
    {
        if (job.WorkerId != workerId || job.Attempt != attempt || string.IsNullOrEmpty(token)
            || job.LeaseTokenHash != TranscodeWorkerService.SecretHash(token))
        {
            throw AiErrors.CreateException("AI_LEASE_LOST", 409);
        }
    }

    private static bool Repeated(AiAnalysisJob job, long sequence, string hash)
    {
        if (sequence == job.CheckpointSequence && sequence > 0)
        {
            if (job.CheckpointHash != hash)
            {
                throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
            }
            return true;
        }
        return false;
    }

    private static void Validate(AiAnalysisJob job, AiExecutionState execution, long sequence)
    {
        if (sequence != job.CheckpointSequence + 1 || execution.JobId != job.Id || execution.SessionId != job.SessionId
            || execution.Purpose != job.Purpose || execution.Snapshot.SessionId != job.SessionId
            || execution.Snapshot.SourceFingerprint != job.Snapshot.SourceFingerprint
            || execution.Snapshot.StartedAtUtc != job.Snapshot.StartedAtUtc || execution.Snapshot.DurationMs != job.Snapshot.DurationMs
            || execution.Snapshot.RecordedDurationMs != job.Snapshot.RecordedDurationMs || execution.Snapshot.Title != job.Snapshot.Title
            || execution.Snapshot.TranslationPattern != job.Snapshot.TranslationPattern
            || execution.Result.JobId != job.Id || execution.Result.SessionId != job.SessionId || execution.Result.Purpose != job.Purpose
            || execution.Result.SourceFingerprint != job.Snapshot.SourceFingerprint || execution.Result.Model != job.Settings.Model
            || Hash(execution.Settings) != Hash(AiExecutionMapper.ToSettings(job.Settings))
            || execution.TokenBudget != job.TokenBudget || !double.IsFinite(execution.Progress)
            || execution.PromptTokens < 0 || execution.CompletionTokens < 0 || execution.EstimatedTokens < 0 || execution.ReservedTokens < 0
            || execution.ModelRequests < 0 || execution.RequestRetries < 0 || execution.ToolCalls < 0
            || job.ReportOnlyRecovery && (execution.RequestInFlight || execution.ModelRequests > job.ModelRequests))
        {
            throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
        }
    }

    private static void Apply(AiAnalysisJob job, AiExecutionState execution)
    {
        var incoming = AiExecutionMapper.ToJob(execution);
        var path = job.Context.ConversationPath;
        job.Context = incoming.Context;
        job.Context.ConversationPath = path;
        job.Snapshot = incoming.Snapshot;
        job.Snapshot.EvidenceRelativePath = job.Input?.PreparedSnapshot == true ? job.SourcePaths.FirstOrDefault() ?? "" : "";
        job.Result = incoming.Result;
        job.Progress = Math.Clamp(incoming.Progress, 0, 100);
        job.ErrorCode = incoming.ErrorCode;
        job.PromptTokens = incoming.PromptTokens;
        job.CompletionTokens = incoming.CompletionTokens;
        job.EstimatedTokens = incoming.EstimatedTokens;
        job.ReservedTokens = incoming.ReservedTokens;
        job.ModelRequests = incoming.ModelRequests;
        job.RequestRetries = incoming.RequestRetries;
        job.ToolCalls = incoming.ToolCalls;
        job.RequestInFlight = incoming.RequestInFlight;
        if (!job.RequestInFlight)
        {
            job.UnsettledModelRequest = false;
            job.UnsettledReservedTokens = 0;
        }
        job.LastProgressAtUtc = DateTime.UtcNow;
    }

    private async Task SaveMemoryAsync(AiAnalysisJob job, AiAnalysisMemory? memory, long sequence, CancellationToken cancellationToken)
    {
        if (memory is null)
        {
            return;
        }
        if (memory.JobId != job.Id || memory.SessionId != job.SessionId || memory.Version < 0)
        {
            throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
        }
        var relative = Path.Combine("ai", "contexts", job.Id, job.Attempt.ToString(CultureInfo.InvariantCulture) + "-" + sequence.ToString(CultureInfo.InvariantCulture) + ".json");
        await JsonFiles.WriteAsync(MediaPaths.Resolve(paths.DataDirectory, relative), memory, cancellationToken);
        job.Context.ConversationPath = relative;
        job.Context.ConversationVersion = memory.Version;
    }

    private async Task PublishPendingAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        if (!job.PublicationStarted || job.State == "Completed")
        {
            return;
        }
        try
        {
            var id = await liveMarkers.PublishAsync(job, cancellationToken);
            job.LiveMarkerFileId = id;
            job.Result.LiveMarkerFileId = id;
            job.State = "Completed";
            job.Context.Phase = "Completed";
            job.Progress = 100;
        }
        catch (Exception error) when (error is InfrastructureException or IOException or InvalidDataException)
        {
            job.State = "Failed";
            job.ErrorCode = "AI_LIVE_MARKER_SAVE_FAILED";
            job.Result.IsComplete = false;
            job.Result.CompletedAtUtc = null;
            job.LiveMarkerFileId = null;
            job.Result.LiveMarkerFileId = null;
        }
        Finalize(job);
        await PersistAsync(job, cancellationToken);
    }

    private async Task InterruptAsync(AiAnalysisJob job, string code, CancellationToken cancellationToken)
    {
        job.State = "Interrupted";
        job.ErrorCode = code;
        if (job.RequestInFlight)
        {
            job.UnsettledModelRequest = true;
            job.UnsettledReservedTokens = job.ReservedTokens;
            job.EstimatedTokens += job.ReservedTokens;
            job.ReservedTokens = 0;
            job.RequestInFlight = false;
        }
        job.Result.IsComplete = false;
        job.Result.CompletedAtUtc = null;
        job.ActiveKey = null;
        job.LeaseUntilUtc = null;
        job.FinishedAtUtc = DateTime.UtcNow;
        await PersistAsync(job, cancellationToken);
    }

    private static void Finalize(AiAnalysisJob job)
    {
        if (job.RequestInFlight)
        {
            job.UnsettledModelRequest = true;
            job.UnsettledReservedTokens = job.ReservedTokens;
            job.EstimatedTokens += job.ReservedTokens;
            job.ReservedTokens = 0;
            job.RequestInFlight = false;
        }
        job.ActiveKey = null;
        job.LeaseOwner = null;
        job.LeaseUntilUtc = null;
        job.FinishedAtUtc = DateTime.UtcNow;
    }

    private async Task PersistAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var cancelled = job.State == "Cancelled";
        var changed = await Jobs.UpdateOneAsync(value => value.Id == job.Id && value.Attempt == job.Attempt
                && (!value.CancelRequested || value.PublicationStarted || cancelled),
            Builders<AiAnalysisJob>.Update.Set(value => value.State, job.State).Set(value => value.ActiveKey, job.ActiveKey)
                .Set(value => value.WorkerId, job.WorkerId).Set(value => value.LeaseTokenHash, job.LeaseTokenHash)
                .Set(value => value.LeaseOwner, job.LeaseOwner).Set(value => value.LeaseUntilUtc, job.LeaseUntilUtc)
                .Set(value => value.ReportOnlyRecovery, job.ReportOnlyRecovery)
                .Set(value => value.UnsettledModelRequest, job.UnsettledModelRequest)
                .Set(value => value.UnsettledReservedTokens, job.UnsettledReservedTokens)
                .Set(value => value.Input, job.Input).Set(value => value.SourcePaths, job.SourcePaths)
                .Set(value => value.Context, job.Context).Set(value => value.Result, job.Result).Set(value => value.Snapshot, job.Snapshot)
                .Set(value => value.Progress, job.Progress).Set(value => value.ErrorCode, job.ErrorCode)
                .Set(value => value.PromptTokens, job.PromptTokens).Set(value => value.CompletionTokens, job.CompletionTokens)
                .Set(value => value.EstimatedTokens, job.EstimatedTokens).Set(value => value.ReservedTokens, job.ReservedTokens)
                .Set(value => value.ModelRequests, job.ModelRequests).Set(value => value.RequestRetries, job.RequestRetries)
                .Set(value => value.ToolCalls, job.ToolCalls).Set(value => value.RequestInFlight, job.RequestInFlight)
                .Set(value => value.CheckpointSequence, job.CheckpointSequence).Set(value => value.CheckpointHash, job.CheckpointHash)
                .Set(value => value.PublicationStarted, job.PublicationStarted).Set(value => value.LiveMarkerFileId, job.LiveMarkerFileId)
                .Set(value => value.FinishedAtUtc, job.FinishedAtUtc).Set(value => value.LastProgressAtUtc, job.LastProgressAtUtc)
                .Set(value => value.Stages, job.Stages), cancellationToken: cancellationToken);
        if (changed.MatchedCount == 0)
        {
            await Jobs.UpdateOneAsync(value => value.Id == job.Id && value.CancelRequested && !value.PublicationStarted,
                Builders<AiAnalysisJob>.Update.Set(value => value.State, "Cancelled").Set(value => value.ActiveKey, null)
                    .Set(value => value.ErrorCode, "AI_CANCELLED").Set(value => value.Result.IsComplete, false)
                    .Set(value => value.Result.CompletedAtUtc, null).Set(value => value.FinishedAtUtc, DateTime.UtcNow), cancellationToken: cancellationToken);
            throw AiErrors.CreateException("AI_LEASE_LOST", 409);
        }
        CleanupMemory(job.Context.ConversationPath);
        await publisher.PublishAsync("ai-analysis", job.SessionId, cancellationToken: cancellationToken);
    }

    private void CleanupMemory(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return;
        }
        var current = MediaPaths.Resolve(paths.DataDirectory, relative);
        var directory = Path.GetDirectoryName(current)!;
        foreach (var other in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!string.Equals(other, current, StringComparison.Ordinal))
            {
                File.Delete(other);
            }
        }
    }

    private static FilterDefinition<AiAnalysisJob> Fence(AiAnalysisJob job) =>
        Builders<AiAnalysisJob>.Filter.Where(value => value.Id == job.Id && value.WorkerId == job.WorkerId
            && value.Attempt == job.Attempt && value.LeaseTokenHash == job.LeaseTokenHash);

    private Task<FileStream> LockAsync(string jobId, CancellationToken cancellationToken) =>
        MediaJobExecutionLock.AcquireAsync(paths, "ai-" + TranscodeAssetRegistry.Hash(jobId), cancellationToken);

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions)));
}
