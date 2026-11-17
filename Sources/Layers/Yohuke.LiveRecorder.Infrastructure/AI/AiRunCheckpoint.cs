using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

internal sealed class AiRunCheckpoint(IMongoContext context, ISettingsStore settingsStore, IStatusPublisher publisher, string owner, IAppPaths paths) : IAiRunCheckpoint
{
    public async Task SaveAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        await RequireExecutionAsync(job, cancellationToken);
        var updated = await context.Collection<AiAnalysisJob>("ai_analysis_jobs").UpdateOneAsync(
            item => item.Id == job.Id && item.State == "Running" && item.LeaseOwner == owner && item.LeaseUntilUtc > DateTime.UtcNow && !item.CancelRequested,
            Builders<AiAnalysisJob>.Update.Set(item => item.Result, job.Result).Set(item => item.Context, job.Context).Set(item => item.PromptTokens, job.PromptTokens)
                .Set(item => item.CompletionTokens, job.CompletionTokens).Set(item => item.EstimatedTokens, job.EstimatedTokens)
                .Set(item => item.ModelRequests, job.ModelRequests).Set(item => item.ToolCalls, job.ToolCalls)
                .Set(item => item.RequestRetries, job.RequestRetries)
                .Set(item => item.ReservedTokens, job.ReservedTokens).Set(item => item.RequestInFlight, job.RequestInFlight)
                .Set(item => item.Progress, job.Progress).Set(item => item.ErrorCode, job.ErrorCode), cancellationToken: cancellationToken);
        if (updated.MatchedCount == 0)
        {
            throw new AiExecutionStoppedException("AI_LEASE_LOST");
        }
        await publisher.PublishAsync("ai-analysis", job.SessionId, cancellationToken: cancellationToken);
    }

    public async Task SaveMemoryAsync(AiAnalysisJob job, AiAnalysisMemory memory, CancellationToken cancellationToken)
    {
        await RequireExecutionAsync(job, cancellationToken);
        var previous = job.Context.ConversationPath;
        var previousVersion = job.Context.ConversationVersion;
        var relative = Path.Combine("ai", "contexts", job.Id, Guid.NewGuid().ToString("N") + ".json");
        var output = MediaPaths.Resolve(paths.DataDirectory, relative);
        memory.Version = previousVersion + 1;
        memory.JobId = job.Id;
        memory.SessionId = job.SessionId;
        await JsonFiles.WriteAsync(output, memory, cancellationToken);
        job.Context.ConversationPath = relative;
        job.Context.ConversationVersion = memory.Version;
        try
        {
            await SaveAsync(job, cancellationToken);
        }
        catch
        {
            using var verify = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var saved = await context.Collection<AiAnalysisJob>("ai_analysis_jobs").Find(item => item.Id == job.Id).FirstOrDefaultAsync(verify.Token);
                if (saved?.Context.ConversationPath != relative)
                {
                    File.Delete(output);
                    job.Context.ConversationPath = previous;
                    job.Context.ConversationVersion = previousVersion;
                }
                else
                {
                    RemoveOtherContextVersions(output);
                }
            }
            catch (Exception error) when (error is MongoException or OperationCanceledException)
            {
            }
            throw;
        }
        RemoveOtherContextVersions(output);
    }

    private static void RemoveOtherContextVersions(string current)
    {
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(current)!, "*.json"))
        {
            if (!string.Equals(path, current, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
    }

    public async Task<AiAnalysisMemory> LoadMemoryAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        if (job.Context.ConversationPath is null)
        {
            return new();
        }
        var path = MediaPaths.Resolve(paths.DataDirectory, job.Context.ConversationPath);
        await using var input = File.OpenRead(path);
        var memory = await JsonSerializer.DeserializeAsync<AiAnalysisMemory>(input, JsonFiles.Options, cancellationToken)
                     ?? throw new InvalidDataException("Missing AI context.");
        if (memory.JobId != job.Id || memory.SessionId != job.SessionId || memory.Version != job.Context.ConversationVersion)
        {
            throw new InvalidDataException("AI context checkpoint does not match its job.");
        }
        return memory;
    }

    internal async Task RequireExecutionAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var settings = (await settingsStore.GetAsync(cancellationToken)).Ai;
        if (!settings.Enabled)
        {
            throw new AiExecutionStoppedException("AI_DISABLED");
        }
        if (settings.Model != job.Settings.Model || settings.BaseUrl != job.Settings.BaseUrl || settings.Provider != job.Settings.Provider ||
            settings.ThinkingMode != job.Settings.ThinkingMode || settings.ReasoningEffort != job.Settings.ReasoningEffort ||
            settings.RequestTimeoutSeconds != job.Settings.RequestTimeoutSeconds || settings.MaxRequestRetries != job.Settings.MaxRequestRetries ||
            AiSettingsSecrets.Fingerprint(settings) != job.CredentialFingerprint)
        {
            throw new AiExecutionStoppedException("AI_CONFIGURATION_CHANGED");
        }
        if (!await context.Collection<BroadcastSession>("sessions").Find(item => item.Id == job.SessionId && !item.IsTrashed && !item.IsHidden && item.ProjectionReady).AnyAsync(cancellationToken))
        {
            throw new AiExecutionStoppedException("AI_SOURCE_UNAVAILABLE");
        }
        if (!await context.Collection<AiExecutionLease>("ai_execution_leases").Find(item => item.Id == "global" && item.Owner == owner && item.UntilUtc > DateTime.UtcNow).AnyAsync(cancellationToken))
        {
            throw new AiExecutionStoppedException("AI_LEASE_LOST");
        }
    }
}
