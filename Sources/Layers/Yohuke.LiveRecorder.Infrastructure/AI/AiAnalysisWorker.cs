using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>以全局单任务租约运行分析，不重放结果未知的模型请求。</summary>
public sealed class AiAnalysisWorker(IMongoContext context, IBootstrapStore bootstrap, ISettingsStore settingsStore,
    ISecretProtector protector, IAiAnalysisRunner runner, IStatusPublisher publisher, ILogger<AiAnalysisWorker> logger, IAppPaths paths,
    AiLiveMarkerPublisher liveMarkers) : BackgroundService
{
    private readonly string owner = Guid.NewGuid().ToString("N");
    private IMongoCollection<AiAnalysisJob> Jobs => context.Collection<AiAnalysisJob>("ai_analysis_jobs");
    private IMongoCollection<AiExecutionLease> Leases => context.Collection<AiExecutionLease>("ai_execution_leases");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (bootstrap.IsInstalled && await AcquireLeaseAsync(stoppingToken))
                {
                    try
                    {
                        await RecoverAsync(stoppingToken);
                        var settings = await settingsStore.GetAsync(stoppingToken);
                        if (settings.Ai.Enabled)
                        {
                            var job = await Jobs.FindOneAndUpdateAsync(item => item.State == "Queued" && !item.CancelRequested,
                                Builders<AiAnalysisJob>.Update.Set(item => item.State, "Running").Set(item => item.LeaseOwner, owner)
                                    .Set(item => item.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(2)),
                                new FindOneAndUpdateOptions<AiAnalysisJob> { ReturnDocument = ReturnDocument.After, Sort = Builders<AiAnalysisJob>.Sort.Ascending(item => item.CreatedAtUtc) }, stoppingToken);
                            if (job is not null)
                            {
                                await RunAsync(job, stoppingToken);
                            }
                        }
                    }
                    finally
                    {
                        await Leases.UpdateOneAsync(item => item.Id == "global" && item.Owner == owner,
                            Builders<AiExecutionLease>.Update.Set(item => item.UntilUtc, DateTime.UtcNow), cancellationToken: CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("AI 调度等待恢复：{ErrorType}", error.GetType().Name);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Leases.UpdateOneAsync(item => item.Id == "global", Builders<AiExecutionLease>.Update
                .SetOnInsert(item => item.Owner, "").SetOnInsert(item => item.UntilUtc, DateTime.MinValue),
                new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }
        var lease = await Leases.FindOneAndUpdateAsync(item => item.Id == "global" && (item.UntilUtc < DateTime.UtcNow || item.Owner == owner),
            Builders<AiExecutionLease>.Update.Set(item => item.Owner, owner).Set(item => item.UntilUtc, DateTime.UtcNow.AddMinutes(2)),
            new FindOneAndUpdateOptions<AiExecutionLease> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        return lease is not null;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var expired = await Jobs.Find(item => item.State == "Running" && item.LeaseUntilUtc < DateTime.UtcNow).Limit(50).ToListAsync(cancellationToken);
        foreach (var job in expired)
        {
            try
            {
                await RecoverJobAsync(job, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning("AI 作业 {JobId} 恢复保存待重试：{ErrorType}", job.Id, error.GetType().Name);
            }
        }
    }

    private async Task RecoverJobAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var complete = job.Result.IsComplete && !job.RequestInFlight && (job.Context.Phase is "Completed" or "Publishing") &&
                       job.Context.Segments.Count > 0 && job.Context.Segments.All(segment => segment.State is "Completed" or "ReviewedNoTopics" or "Empty");
        if (complete && job.Purpose == AiJobPurposes.LIVE_MARKERS && (!job.CancelRequested || job.PublicationStarted))
        {
            var claimed = await Jobs.FindOneAndUpdateAsync(item => item.Id == job.Id && item.State == "Running" && item.LeaseUntilUtc < DateTime.UtcNow &&
                    (!item.CancelRequested || item.PublicationStarted),
                Builders<AiAnalysisJob>.Update.Set(item => item.LeaseOwner, owner).Set(item => item.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(2))
                    .Set(item => item.PublicationStarted, true).Set(item => item.Context.Phase, "Publishing"),
                new FindOneAndUpdateOptions<AiAnalysisJob> { ReturnDocument = ReturnDocument.After }, cancellationToken);
            if (claimed is not null)
            {
                await PublishFileAsync(claimed);
                await Jobs.UpdateOneAsync(item => item.Id == claimed.Id && item.State == "Running" && item.LeaseOwner == owner && item.PublicationStarted,
                    FinalUpdate(claimed), cancellationToken: CancellationToken.None);
                await publisher.PublishAsync("ai-analysis", claimed.SessionId, cancellationToken: CancellationToken.None);
            }
            return;
        }
        var state = job.CancelRequested ? "Cancelled" : complete ? "Completed" : "Interrupted";
        await Jobs.UpdateOneAsync(item => item.Id == job.Id && item.State == "Running" && item.LeaseUntilUtc < DateTime.UtcNow,
            Builders<AiAnalysisJob>.Update.Set(item => item.State, state).Set(item => item.ActiveKey, null)
                .Inc(item => item.EstimatedTokens, job.RequestInFlight ? job.ReservedTokens : 0)
                .Set(item => item.ReservedTokens, 0).Set(item => item.RequestInFlight, false)
                .Set(item => item.LeaseOwner, null).Set(item => item.LeaseUntilUtc, null)
                .Set(item => item.Result.IsComplete, complete && !job.CancelRequested)
                .Set(item => item.ErrorCode, job.CancelRequested ? "AI_CANCELLED" : complete ? null : "AI_PROCESS_INTERRUPTED")
                .Set(item => item.FinishedAtUtc, DateTime.UtcNow), cancellationToken: cancellationToken);
        await publisher.PublishAsync("ai-analysis", job.SessionId, cancellationToken: cancellationToken);
    }

    private async Task RunAsync(AiAnalysisJob job, CancellationToken stoppingToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var checkpoint = new AiRunCheckpoint(context, settingsStore, publisher, owner, paths);
        var heartbeat = HeartbeatAsync(job, checkpoint, run);
        try
        {
            await checkpoint.RequireExecutionAsync(job, run.Token);
            var settings = (await settingsStore.GetAsync(run.Token)).Ai;
            var apiKey = string.IsNullOrEmpty(settings.ApiKey) ? "" : protector.Unprotect(settings.ApiKey);
            await runner.RunAsync(job, apiKey, checkpoint, run.Token);
        }
        catch (OperationCanceledException error)
        {
            var current = await Jobs.Find(item => item.Id == job.Id).FirstOrDefaultAsync(CancellationToken.None);
            job.State = current?.CancelRequested == true ? "Cancelled" : "Interrupted";
            job.ErrorCode = error is AiExecutionStoppedException stopped ? stopped.Code : stoppingToken.IsCancellationRequested ? "AI_PROCESS_INTERRUPTED" : "AI_CANCELLED";
        }
        catch (Exception error)
        {
            job.State = job.RequestInFlight ? "Interrupted" : "Failed";
            job.ErrorCode = job.RequestInFlight ? "AI_REQUEST_UNKNOWN" : "AI_EXECUTION_FAILED";
            logger.LogWarning("AI 作业 {JobId} 停止：{ErrorType}", job.Id, error.GetType().Name);
        }
        finally
        {
            await run.CancelAsync();
            await heartbeat;
        }
        if (job.RequestInFlight)
        {
            job.EstimatedTokens += job.ReservedTokens;
            job.ReservedTokens = 0;
            job.RequestInFlight = false;
        }
        if (job.State == "Running")
        {
            job.State = "Partial";
        }
        if (job.State != "Completed")
        {
            job.Result.IsComplete = false;
            job.Result.CompletedAtUtc = null;
        }
        if (!await Leases.Find(item => item.Id == "global" && item.Owner == owner && item.UntilUtc > DateTime.UtcNow).AnyAsync(CancellationToken.None))
        {
            return;
        }
        if (job.State == "Completed" && job.Purpose == AiJobPurposes.LIVE_MARKERS)
        {
            var begun = await Jobs.UpdateOneAsync(item => item.Id == job.Id && item.State == "Running" && item.LeaseOwner == owner &&
                    item.LeaseUntilUtc > DateTime.UtcNow && !item.CancelRequested && !item.PublicationStarted,
                Builders<AiAnalysisJob>.Update.Set(item => item.PublicationStarted, true).Set(item => item.Context.Phase, "Publishing")
                    .Set(item => item.Result, job.Result), cancellationToken: CancellationToken.None);
            if (begun.MatchedCount > 0)
            {
                job.PublicationStarted = true;
                job.Context.Phase = "Publishing";
                await publisher.PublishAsync("ai-analysis", job.SessionId, cancellationToken: CancellationToken.None);
                await PublishFileAsync(job);
            }
        }
        var filter = Builders<AiAnalysisJob>.Filter.Where(item => item.Id == job.Id && item.State == "Running" &&
            item.LeaseOwner == owner && item.LeaseUntilUtc > DateTime.UtcNow);
        var finished = await Jobs.UpdateOneAsync(filter & Builders<AiAnalysisJob>.Filter.Eq(item => item.CancelRequested, false),
            FinalUpdate(job), cancellationToken: CancellationToken.None);
        if (finished.MatchedCount == 0 && !job.PublicationStarted)
        {
            job.State = "Cancelled";
            job.ErrorCode = "AI_CANCELLED";
            job.Result.IsComplete = false;
            job.Result.CompletedAtUtc = null;
            finished = await Jobs.UpdateOneAsync(filter & Builders<AiAnalysisJob>.Filter.Eq(item => item.CancelRequested, true),
                FinalUpdate(job), cancellationToken: CancellationToken.None);
        }
        if (finished.MatchedCount > 0)
        {
            await publisher.PublishAsync("ai-analysis", job.SessionId);
        }
    }

    private static UpdateDefinition<AiAnalysisJob> FinalUpdate(AiAnalysisJob job) =>
        Builders<AiAnalysisJob>.Update.Set(item => item.State, job.State).Set(item => item.Result, job.Result).Set(item => item.Context, job.Context)
                .Set(item => item.PromptTokens, job.PromptTokens).Set(item => item.CompletionTokens, job.CompletionTokens)
                .Set(item => item.EstimatedTokens, job.EstimatedTokens).Set(item => item.ReservedTokens, 0)
                .Set(item => item.ModelRequests, job.ModelRequests).Set(item => item.ToolCalls, job.ToolCalls)
                .Set(item => item.RequestRetries, job.RequestRetries)
                .Set(item => item.RequestInFlight, false).Set(item => item.Progress, job.Progress)
                .Set(item => item.ErrorCode, job.ErrorCode).Set(item => item.FinishedAtUtc, DateTime.UtcNow)
                .Set(item => item.PublicationStarted, job.PublicationStarted).Set(item => item.LiveMarkerFileId, job.LiveMarkerFileId)
                .Set(item => item.ActiveKey, null).Set(item => item.LeaseOwner, null).Set(item => item.LeaseUntilUtc, null);

    private async Task PublishFileAsync(AiAnalysisJob job)
    {
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = PublicationHeartbeatAsync(job.Id, heartbeatStop.Token);
        try
        {
            var id = await liveMarkers.PublishAsync(job, CancellationToken.None);
            job.LiveMarkerFileId = id;
            job.Result.LiveMarkerFileId = id;
            job.State = "Completed";
            job.Context.Phase = "Completed";
            job.Progress = 100;
            job.ErrorCode = null;
        }
        catch (Exception error) when (error is not MongoException)
        {
            job.State = "Failed";
            job.ErrorCode = "AI_LIVE_MARKER_SAVE_FAILED";
            job.Result.IsComplete = false;
            job.Result.CompletedAtUtc = null;
            logger.LogWarning("AI 直播记录 {JobId} 保存失败：{ErrorType}", job.Id, error.GetType().Name);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
    }

    private async Task PublicationHeartbeatAsync(string jobId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var now = DateTime.UtcNow;
                var global = await Leases.UpdateOneAsync(item => item.Id == "global" && item.Owner == owner && item.UntilUtc > now,
                    Builders<AiExecutionLease>.Update.Set(item => item.UntilUtc, now.AddMinutes(2)), cancellationToken: cancellationToken);
                if (global.MatchedCount == 0)
                {
                    return;
                }
                var lease = await Jobs.UpdateOneAsync(item => item.Id == jobId && item.State == "Running" && item.LeaseOwner == owner && item.PublicationStarted,
                    Builders<AiAnalysisJob>.Update.Set(item => item.LeaseUntilUtc, now.AddMinutes(2)), cancellationToken: cancellationToken);
                if (lease.MatchedCount == 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning("AI 直播记录保存续租停止：{ErrorType}", error.GetType().Name);
        }
    }

    private async Task HeartbeatAsync(AiAnalysisJob job, AiRunCheckpoint checkpoint, CancellationTokenSource run)
    {
        try
        {
            while (!run.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), run.Token);
                await checkpoint.RequireExecutionAsync(job, run.Token);
                var now = DateTime.UtcNow;
                var global = await Leases.UpdateOneAsync(item => item.Id == "global" && item.Owner == owner && item.UntilUtc > now,
                    Builders<AiExecutionLease>.Update.Set(item => item.UntilUtc, now.AddMinutes(2)), cancellationToken: run.Token);
                var lease = await Jobs.UpdateOneAsync(item => item.Id == job.Id && item.State == "Running" && item.LeaseOwner == owner && !item.CancelRequested && item.LeaseUntilUtc > now,
                    Builders<AiAnalysisJob>.Update.Set(item => item.LeaseUntilUtc, now.AddMinutes(2)), cancellationToken: run.Token);
                if (global.MatchedCount == 0 || lease.MatchedCount == 0)
                {
                    await run.CancelAsync();
                }
            }
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
            {
                logger.LogWarning("AI 作业续租停止：{ErrorType}", error.GetType().Name);
            }
            await run.CancelAsync();
        }
    }
}
