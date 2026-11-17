using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>建立共享分析快照并管理独立、可取消的持久作业。</summary>
public sealed class AiAnalysisService(IMongoContext context, LibraryService library, ISettingsStore settingsStore,
    AiArchiveDataSource data, IStatusPublisher publisher, AiLiveMarkerPublisher liveMarkers)
{
    private readonly SemaphoreSlim creationGate = new(1, 1);
    private IMongoCollection<AiAnalysisJob> Jobs => context.Collection<AiAnalysisJob>("ai_analysis_jobs");

    /// <summary>读取可见录像的共享 AI 后台任务，仅投影公开进度字段。</summary>
    public Task<IReadOnlyList<AiAnalysisJobSummary>> ListAsync(CancellationToken cancellationToken) => ListAsync(true, cancellationToken);

    /// <summary>按用途筛选可见任务，直播记录不要求统计查看权限。</summary>
    public async Task<IReadOnlyList<AiAnalysisJobSummary>> ListAsync(bool canViewAnalytics, CancellationToken cancellationToken)
    {
        var sessions = context.Collection<BroadcastSession>("sessions").AsQueryable();
        var jobs = Jobs.AsQueryable();
        if (!canViewAnalytics)
        {
            jobs = jobs.Where(job => job.Purpose == AiJobPurposes.LIVE_MARKERS);
        }
        var query = from job in jobs
            join session in sessions on job.SessionId equals session.Id
            where !session.IsTrashed && !session.IsHidden && session.ProjectionReady
            orderby job.CreatedAtUtc descending, job.Id descending
            select new AiAnalysisJob
            {
                Id = job.Id, SessionId = job.SessionId, State = job.State, Progress = job.Progress,
                PromptTokens = job.PromptTokens, CompletionTokens = job.CompletionTokens, EstimatedTokens = job.EstimatedTokens,
                TokenBudget = job.TokenBudget, ModelRequests = job.ModelRequests, ToolCalls = job.ToolCalls,
                RequestRetries = job.RequestRetries,
                ErrorCode = job.ErrorCode, CreatedAtUtc = job.CreatedAtUtc, FinishedAtUtc = job.FinishedAtUtc,
                CancelRequested = job.CancelRequested, Context = job.Context,
                Purpose = job.Purpose, LiveMarkerFileId = job.LiveMarkerFileId,
                Snapshot = new AiAnalysisSnapshot { Title = session.Title }
            };
        return (await query.Take(200).ToListAsync(cancellationToken)).Select(AiAnalysisJobSummary.From).ToArray();
    }

    /// <summary>读取最新完整报告和增量报告；读取不会触发模型请求。</summary>
    public Task<AiAnalysisStatus> GetAsync(string sessionId, bool canGenerate, CancellationToken cancellationToken) =>
        GetAsync(sessionId, AiJobPurposes.ANALYSIS, canGenerate, cancellationToken);

    /// <summary>读取指定用途的共享结果，不混用统计分析与记录生成任务。</summary>
    public async Task<AiAnalysisStatus> GetAsync(string sessionId, string purpose, bool canGenerate, CancellationToken cancellationToken)
    {
        RequirePurpose(purpose);
        var detail = await library.GetAsync(sessionId, cancellationToken);
        RequireVisible(detail.Session);
        var settings = await settingsStore.GetAsync(cancellationToken);
        var filter = ForPurpose(purpose) & Builders<AiAnalysisJob>.Filter.Eq(item => item.SessionId, sessionId);
        var jobs = await Jobs.Find(filter).SortByDescending(item => item.CreatedAtUtc).Limit(30).ToListAsync(cancellationToken);
        var completed = await Jobs.Find(filter & Builders<AiAnalysisJob>.Filter.Eq(item => item.State, "Completed"))
            .SortByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var partial = jobs.FirstOrDefault(item => item.State != "Completed" && HasContent(item) &&
            (completed is null || item.CreatedAtUtc > completed.CreatedAtUtc));
        var latest = jobs.FirstOrDefault();
        var identity = AiArchiveDataSource.Identity(data.Fingerprint(detail, settings), settings.Ai, purpose);
        var missingFile = completed is not null && purpose == AiJobPurposes.LIVE_MARKERS &&
                          !await MarkerPublisher.ExistsAsync(sessionId, completed.LiveMarkerFileId, cancellationToken);
        if (missingFile)
        {
            completed!.Result.LiveMarkerFileId = null;
            if (latest?.Id == completed.Id)
            {
                latest.LiveMarkerFileId = null;
            }
        }
        return new()
        {
            Enabled = settings.Ai.Enabled, CanGenerate = canGenerate,
            CurrentJob = latest is null ? null : AiAnalysisJobSummary.From(latest),
            LatestCompleted = completed?.Result, LatestPartial = partial?.Result,
            LatestCompletedStale = completed is not null && (completed.Identity != identity || missingFile),
            LatestPartialStale = partial is not null && partial.Identity != identity,
            Stale = missingFile || (completed ?? partial) is { } report && report.Identity != identity
        };
    }

    /// <summary>同场在途作业去重，已完成快照仅在显式重分析时重新创建。</summary>
    public Task<AiAnalysisJobSummary> CreateAsync(string sessionId, string ownerId, bool force, CancellationToken cancellationToken) =>
        CreateAsync(sessionId, ownerId, "", AiJobPurposes.ANALYSIS, force, cancellationToken);

    /// <summary>为指定用途创建独立任务并固定发起者姓名；强制重生成使用新的文件身份。</summary>
    public async Task<AiAnalysisJobSummary> CreateAsync(string sessionId, string ownerId, string ownerName, string purpose, bool force, CancellationToken cancellationToken)
    {
        RequirePurpose(purpose);
        await creationGate.WaitAsync(cancellationToken);
        try
        {
            var detail = await library.GetAsync(sessionId, cancellationToken);
            RequireVisible(detail.Session);
            if (detail.Session.EndedAtUtc is null)
            {
                throw AiErrors.CreateException("AI_RECORDING_NOT_FINISHED", 409);
            }
            var settings = await settingsStore.GetAsync(cancellationToken);
            AiAnalysisPolicy.Validate(settings.Ai);
            if (!settings.Ai.Enabled)
            {
                throw AiErrors.CreateException("AI_DISABLED", 409);
            }
            if (!double.IsFinite(detail.DurationMs) || detail.DurationMs <= 0)
            {
                throw AiErrors.CreateException("AI_RECORDING_EMPTY", 409);
            }
            await EnsureIndexesAsync(cancellationToken);
            var activeKey = purpose == AiJobPurposes.ANALYSIS ? sessionId : purpose + ":" + sessionId;
            var purposeFilter = ForPurpose(purpose);
            var active = await Jobs.Find(purposeFilter & Builders<AiAnalysisJob>.Filter.Eq(item => item.ActiveKey, activeKey)).FirstOrDefaultAsync(cancellationToken);
            if (active is not null)
            {
                return AiAnalysisJobSummary.From(active);
            }
            var fingerprint = data.Fingerprint(detail, settings);
            var identity = AiArchiveDataSource.Identity(fingerprint, settings.Ai, purpose);
            if (!force && await Jobs.Find(purposeFilter & Builders<AiAnalysisJob>.Filter.Where(item => item.SessionId == sessionId && item.Identity == identity && item.State == "Completed"))
                    .SortByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken) is { } existing)
            {
                if (purpose == AiJobPurposes.LIVE_MARKERS && !await MarkerPublisher.ExistsAsync(sessionId, existing.LiveMarkerFileId, cancellationToken))
                {
                    throw AiErrors.CreateException("AI_LIVE_MARKER_FILE_MISSING", 409);
                }
                return AiAnalysisJobSummary.From(existing);
            }
            var snapshot = await data.CreateAsync(detail, settings, cancellationToken);
            identity = AiArchiveDataSource.Identity(snapshot.SourceFingerprint, settings.Ai, purpose);
            var configuration = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(settings.Ai, JsonFiles.Options), JsonFiles.Options)!;
            configuration.ApiKey = "";
            configuration.HasApiKey = false;
            configuration.ClearApiKey = false;
            var job = new AiAnalysisJob
            {
                SessionId = sessionId, OwnerId = ownerId, OwnerName = ownerName, Purpose = purpose, ActiveKey = activeKey, Identity = identity,
                CredentialFingerprint = AiSettingsSecrets.Fingerprint(settings.Ai),
                Snapshot = snapshot, Settings = configuration, TokenBudget = AiAnalysisPolicy.Budget(settings.Ai, snapshot.RecordedDurationMs)
            };
            job.Result = new() { JobId = job.Id, SessionId = sessionId, Purpose = purpose, SourceFingerprint = snapshot.SourceFingerprint, Model = configuration.Model };
            try
            {
                await Jobs.InsertOneAsync(job, cancellationToken: cancellationToken);
            }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                data.DeleteSnapshot(snapshot);
                var concurrent = await Jobs.Find(purposeFilter & Builders<AiAnalysisJob>.Filter.Eq(item => item.ActiveKey, activeKey)).FirstOrDefaultAsync(cancellationToken);
                return concurrent is null ? throw AiErrors.CreateException("AI_JOB_CONFLICT", 409) : AiAnalysisJobSummary.From(concurrent);
            }
            catch
            {
                await RemoveUnreferencedSnapshotAsync(job);
                throw;
            }
            await publisher.PublishAsync("ai-analysis", sessionId, cancellationToken: cancellationToken);
            return AiAnalysisJobSummary.From(job);
        }
        finally
        {
            creationGate.Release();
        }
    }

    private async Task RemoveUnreferencedSnapshotAsync(AiAnalysisJob job)
    {
        using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var referenced = await Jobs.Find(item => item.Id == job.Id).AnyAsync(verification.Token);
            if (!referenced)
            {
                data.DeleteSnapshot(job.Snapshot);
            }
        }
        catch (Exception error) when (error is MongoException or OperationCanceledException)
        {
        }
    }

    /// <summary>取消排队任务或通知后台执行器取消当前请求。</summary>
    public Task CancelAsync(string id, CancellationToken cancellationToken) => CancelAsync(id, true, true, cancellationToken);

    /// <summary>按真实任务用途校验取消权限，发布提交开始后拒绝取消。</summary>
    public async Task CancelAsync(string id, bool canCancelAnalysis, bool canCancelLiveMarkers, CancellationToken cancellationToken)
    {
        var job = await Jobs.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken)
                  ?? throw AiErrors.CreateException("AI_JOB_NOT_FOUND", 404);
        RequireVisible((await library.GetAsync(job.SessionId, cancellationToken)).Session);
        if (job.Purpose == AiJobPurposes.LIVE_MARKERS ? !canCancelLiveMarkers : !canCancelAnalysis)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_ACCESS_DENIED, 403);
        }
        if (job.State == "Running" && job.PublicationStarted)
        {
            throw AiErrors.CreateException("AI_PUBLICATION_IN_PROGRESS", 409);
        }
        await Jobs.UpdateOneAsync(item => item.Id == id && item.State == "Queued", Builders<AiAnalysisJob>.Update
            .Set(item => item.CancelRequested, true).Set(item => item.State, "Cancelled").Set(item => item.ActiveKey, null)
            .Set(item => item.FinishedAtUtc, DateTime.UtcNow), cancellationToken: cancellationToken);
        var cancelled = await Jobs.UpdateOneAsync(item => item.Id == id && item.State == "Running" && !item.PublicationStarted, Builders<AiAnalysisJob>.Update
            .Set(item => item.CancelRequested, true), cancellationToken: cancellationToken);
        if (cancelled.MatchedCount == 0 && await Jobs.Find(item => item.Id == id && item.State == "Running" && item.PublicationStarted).AnyAsync(cancellationToken))
        {
            throw AiErrors.CreateException("AI_PUBLICATION_IN_PROGRESS", 409);
        }
        await publisher.PublishAsync("ai-analysis", job.SessionId, cancellationToken: cancellationToken);
    }

    private AiLiveMarkerPublisher MarkerPublisher => liveMarkers;

    private static bool HasContent(AiAnalysisJob job) => job.Purpose == AiJobPurposes.LIVE_MARKERS ? job.Result.LiveMarkers.Count > 0 : job.Result.Topics.Count > 0;

    private static FilterDefinition<AiAnalysisJob> ForPurpose(string purpose) => purpose == AiJobPurposes.LIVE_MARKERS
        ? Builders<AiAnalysisJob>.Filter.Eq(item => item.Purpose, purpose)
        : Builders<AiAnalysisJob>.Filter.Eq(item => item.Purpose, AiJobPurposes.ANALYSIS) | Builders<AiAnalysisJob>.Filter.Exists("Purpose", false);

    private static void RequirePurpose(string purpose)
    {
        if (purpose is not (AiJobPurposes.ANALYSIS or AiJobPurposes.LIVE_MARKERS))
        {
            throw AiErrors.CreateException("AI_SETTINGS_INVALID");
        }
    }

    private static void RequireVisible(BroadcastSession session)
    {
        if (session.IsHidden || session.IsTrashed || !session.ProjectionReady)
        {
            throw new InfrastructureException(MessageCode.LIVE_MARKER_SESSION_NOT_AVAILABLE, 409);
        }
    }

    private Task EnsureIndexesAsync(CancellationToken cancellationToken) => Jobs.Indexes.CreateOneAsync(new CreateIndexModel<AiAnalysisJob>(
        Builders<AiAnalysisJob>.IndexKeys.Ascending(item => item.ActiveKey), new CreateIndexOptions<AiAnalysisJob>
        {
            Name = "ai_active_session", Unique = true,
            PartialFilterExpression = new BsonDocument("ActiveKey", new BsonDocument("$type", "string"))
        }), cancellationToken: cancellationToken);
}
