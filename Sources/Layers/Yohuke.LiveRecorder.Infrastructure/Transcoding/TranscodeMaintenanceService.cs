using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>恢复结束录播的待调度意图，修复发布中断并回收失效暂存文件。</summary>
public sealed class TranscodeMaintenanceService(IMongoContext context, IBootstrapStore bootstrap, ISettingsStore settingsStore,
    TranscodeJobScheduler scheduler, TranscodeLifecycle lifecycle, TranscodeAssetRegistry assets, IAppPaths paths,
    ILogger<TranscodeMaintenanceService> logger) : BackgroundService
{
    private DateTime nextStagingSweepUtc;
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (bootstrap.IsInstalled)
                {
                    await MaintainAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Transcode scheduling will resume after recovery.");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>运行一次可重复的调度、引用同步和完成清单恢复。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken)
    {
        await assets.EnsureIndexesAsync(cancellationToken);
        var settings = (await settingsStore.GetAsync(cancellationToken)).Transcoding;
        if (settings.Enabled)
        {
            var automaticProfiles = settings.Profiles.Where(x => x.Enabled && x.AutoTranscode).Select(x => x.Id).ToArray();
            await QueueReplacementIntentsAsync(settings, cancellationToken);
            var pending = await context.Collection<BroadcastSession>("sessions")
                .Find(x => x.AutoTranscodeEligible && !x.AutoTranscodeScheduled && x.EndedAtUtc != null
                    && !x.IsTrashed && x.ProjectionReady).Limit(20).ToListAsync(cancellationToken);
            foreach (var session in pending)
            {
                try
                {
                    if (automaticProfiles.Length > 0)
                    {
                        await scheduler.QueueAsync([session.Id], automaticProfiles, false, cancellationToken, allowHidden: true);
                    }
                    await MarkScheduledAsync(session.Id, cancellationToken);
                }
                catch (Exception exception) when (exception is InfrastructureException or FileNotFoundException or InvalidOperationException)
                {
                    logger.LogDebug(exception, "Transcoding for session {SessionId} is waiting for committed media.", session.Id);
                }
            }
        }
        if (await context.Collection<TranscodeAssetReconciliation>("transcodeAssetReconciliation")
            .Find(FilterDefinition<TranscodeAssetReconciliation>.Empty).AnyAsync(cancellationToken))
        {
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            await lifecycle.ReconcileUnderLibraryLockAsync(cancellationToken);
        }
        await RecoverPublishedAsync(cancellationToken);
        await CleanAbandonedPublicationsAsync(cancellationToken);
        if (DateTime.UtcNow >= nextStagingSweepUtc)
        {
            await CleanStagingAsync(cancellationToken);
            nextStagingSweepUtc = DateTime.UtcNow.AddMinutes(5);
        }
    }

    private async Task RecoverPublishedAsync(CancellationToken cancellationToken)
    {
        var incomplete = await context.Collection<TranscodeJob>("transcodeJobs")
            .Find(x => x.State == "Completed" && !x.PublicationReady && !x.CancelRequested)
            .SortBy(x => x.FinishedAtUtc).Limit(100).ToListAsync(cancellationToken);
        foreach (var job in incomplete)
        {
            var gate = WorkerTransferLocks.Gate("Transcoding", job.Id);
            await gate.WaitAsync(cancellationToken);
            try
            {
                await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + job.Id, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.Combine(".derived", job.AssetId, job.SourceVersion, job.RecipeHash,
                    job.Id + "-" + job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var path = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(relative, "completed.json"));
                if (!File.Exists(path))
                {
                    continue;
                }
                await using var stream = File.OpenRead(path);
                var rendition = await JsonSerializer.DeserializeAsync<PlaybackRendition>(stream, JsonFiles.Options, cancellationToken);
                if (rendition is null || rendition.Id != job.Id + "-" + job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || rendition.JobId != job.Id || rendition.Attempt != job.Attempt || rendition.AssetId != job.AssetId
                    || rendition.SourceVersion != job.SourceVersion || rendition.RecipeHash != job.RecipeHash
                    || rendition.RelativeDirectory != relative)
                {
                    continue;
                }
                await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
                var completed = await context.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == rendition.JobId
                    && x.Attempt == rendition.Attempt && x.State == "Completed" && !x.CancelRequested
                    && x.AssetId == rendition.AssetId && x.SourceVersion == rendition.SourceVersion && x.RecipeHash == rendition.RecipeHash).AnyAsync(cancellationToken);
                if (!completed)
                {
                    continue;
                }
                try
                {
                    await assets.RequireActiveAsync(rendition.AssetId, rendition.SourceVersion, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                var directory = MediaPaths.Resolve(paths.RecordsDirectory, rendition.RelativeDirectory);
                foreach (var artifact in rendition.Artifacts)
                {
                    TranscodeArtifactPolicy.ValidateName(artifact.Name);
                }
                if (Path.GetFullPath(Path.Combine(directory, "completed.json")) != Path.GetFullPath(path)
                    || rendition.Artifacts.Any(x => !File.Exists(Path.Combine(directory, x.Name))
                        || new FileInfo(Path.Combine(directory, x.Name)).Length != x.Length
                        || new FileInfo(Path.Combine(directory, x.Name)).LastWriteTimeUtc.Ticks != x.WriteTicks))
                {
                    continue;
                }
                await new TranscodeRenditionStore(context).PublishAsync(rendition, cancellationToken);
                await context.Collection<TranscodeJob>("transcodeJobs").UpdateOneAsync(x => x.Id == job.Id && x.Attempt == job.Attempt && x.State == "Completed",
                    Builders<TranscodeJob>.Update.Set(x => x.PublicationReady, true).Set(x => x.PublicationDirectory, null)
                        .Set(x => x.Stages, WorkerStageSnapshotPolicy.Complete("Transcoding", job.Stages)), cancellationToken: cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task CleanAbandonedPublicationsAsync(CancellationToken cancellationToken)
    {
        var jobs = context.Collection<TranscodeJob>("transcodeJobs");
        var candidates = await jobs.Find(x => x.PublicationDirectory != null && x.State != "Completed").Limit(100).ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            var gate = WorkerTransferLocks.Gate("Transcoding", candidate.Id);
            await gate.WaitAsync(cancellationToken);
            try
            {
                await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + candidate.Id, cancellationToken);
                await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
                var job = await jobs.Find(x => x.Id == candidate.Id && x.PublicationDirectory == candidate.PublicationDirectory).FirstOrDefaultAsync(cancellationToken);
                if (job is null || job.State == "Completed")
                {
                    continue;
                }
                var current = Path.Combine(".derived", job.AssetId, job.SourceVersion, job.RecipeHash,
                    job.Id + "-" + job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (job.PublicationDirectory == current && job.State == "Publishing" && job.LeaseUntilUtc > DateTime.UtcNow && !job.CancelRequested)
                {
                    continue;
                }
                if (!await context.Collection<PlaybackRendition>("playbackRenditions")
                    .Find(x => x.RelativeDirectory == job.PublicationDirectory).AnyAsync(cancellationToken))
                {
                    var directory = MediaPaths.Resolve(paths.RecordsDirectory, job.PublicationDirectory!);
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                    var id = Path.GetFileName(directory);
                    await context.Collection<PlaybackRenditionFile>("renditionFiles").DeleteManyAsync(x => x.RenditionId == id, cancellationToken);
                }
                await jobs.UpdateOneAsync(x => x.Id == job.Id && x.PublicationDirectory == job.PublicationDirectory,
                    Builders<TranscodeJob>.Update.Set(x => x.PublicationDirectory, null), cancellationToken: cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task QueueReplacementIntentsAsync(TranscodingSettings settings, CancellationToken cancellationToken)
    {
        var pending = await context.Collection<RecordingSegment>("segments")
            .Find(x => x.IsClosed && x.PendingTranscodeProfileIds.Any()).SortByDescending(x => x.CreatedAtUtc).Limit(100).ToListAsync(cancellationToken);
        var enabled = settings.Profiles.Where(x => x.Enabled).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var segment in pending)
        {
            var session = await context.Collection<BroadcastSession>("sessions")
                .Find(x => x.Id == segment.SessionId && !x.IsTrashed && x.ProjectionReady).FirstOrDefaultAsync(cancellationToken);
            if (session is null)
            {
                continue;
            }
            if (session.MediaRevision == segment.Revision)
            {
                var profiles = segment.PendingTranscodeProfileIds.Where(enabled.Contains).ToArray();
                if (profiles.Length > 0)
                {
                    await scheduler.QueueAsync([session.Id], profiles, false, cancellationToken, allowHidden: true,
                        assetIds: [TranscodeAssetRegistry.AssetId(session, segment)]);
                }
            }
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            await context.Collection<RecordingSegment>("segments").UpdateOneAsync(x => x.Id == segment.Id && x.Revision == segment.Revision,
                Builders<RecordingSegment>.Update.Set(x => x.PendingTranscodeProfileIds, []), cancellationToken: cancellationToken);
            var current = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == session.Id && !x.IsTrashed).FirstOrDefaultAsync(cancellationToken);
            if (current is not null)
            {
                var all = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == session.Id).ToListAsync(cancellationToken);
                var path = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(current.DirectoryPath, "场次.json"));
                await SessionManifestFiles.WriteAsync(path, new() { Session = current, Segments = all }, cancellationToken);
            }
        }
    }

    private async Task MarkScheduledAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var session = await context.Collection<BroadcastSession>("sessions").FindOneAndUpdateAsync(x => x.Id == sessionId,
            Builders<BroadcastSession>.Update.Set(x => x.AutoTranscodeScheduled, true),
            new FindOneAndUpdateOptions<BroadcastSession> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        if (session is null || session.IsTrashed)
        {
            return;
        }
        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == sessionId).ToListAsync(cancellationToken);
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        if (Directory.Exists(directory))
        {
            await SessionManifestFiles.WriteAsync(Path.Combine(directory, "场次.json"), new() { Session = session, Segments = segments }, cancellationToken);
        }
    }

    private async Task CleanStagingAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(paths.RecordsDirectory, ".work", "transcoding");
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var gate = WorkerTransferLocks.Gate("Transcoding", Path.GetFileName(directory));
            await gate.WaitAsync(cancellationToken);
            try
            {
                await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, "transcoding-" + Path.GetFileName(directory), cancellationToken);
                var id = Path.GetFileName(directory);
                var job = await context.Collection<TranscodeJob>("transcodeJobs").Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
                foreach (var attempt in Directory.EnumerateDirectories(directory))
                {
                    var current = job is not null && Path.GetFileName(attempt) == job.Attempt.ToString()
                        && job.State is "Running" or "Publishing" && job.LeaseUntilUtc > DateTime.UtcNow;
                    if (!current && Directory.GetLastWriteTimeUtc(attempt) < DateTime.UtcNow.AddMinutes(-10))
                    {
                        Directory.Delete(attempt, true);
                    }
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
