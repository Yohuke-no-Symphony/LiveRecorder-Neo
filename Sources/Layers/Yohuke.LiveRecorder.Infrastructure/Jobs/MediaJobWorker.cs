using Yohuke.LiveRecorder.Application.Localization;
using System.Text.Json;
using MongoDB.Bson;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Jobs;

/// <summary>以有效租约、不可变完成清单和有身份的媒体版本执行后台作业。</summary>
public sealed class MediaJobWorker(IMongoContext context, IBootstrapStore bootstrap, ISettingsStore settingsStore, IMediaProcessor processor, IStatusPublisher publisher, StorageService storage, ILogger<MediaJobWorker> logger, IAppPaths paths, ClippingJobScheduler? clipping = null) : BackgroundService
{
    private readonly string workerId = Guid.NewGuid().ToString("N");
    private readonly List<Task> running = [];
    private DateTime lastWorkSweepUtc;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                running.RemoveAll(x => x.IsCompleted);
                if (bootstrap.IsInstalled)
                {
                    var settings = await settingsStore.GetAsync(stoppingToken);
                    if (DateTime.UtcNow - lastWorkSweepUtc > TimeSpan.FromSeconds(30))
                    {
                        await CleanupTerminalWorkAsync(stoppingToken);
                        lastWorkSweepUtc = DateTime.UtcNow;
                    }
                    await FinalizeExpiredCancellationsAsync(stoppingToken);
                    if (running.Count < settings.MediaConcurrency && await storage.GetFreeBytesAsync(stoppingToken) > StorageService.MediaThreshold(settings))
                    {
                        var now = DateTime.UtcNow;
                        var deferred = clipping is null ? Array.Empty<string>() : (await clipping.GetRemoteEligibleJobIdsAsync(stoppingToken)).ToArray();
                        var pending = Builders<MediaJob>.Filter.Where(x => !x.CancelRequested &&
                            (x.State == "Queued" || ((x.State == "Running" || x.State == "Publishing") && (x.LeaseUntilUtc == null || x.LeaseUntilUtc < now))))
                            & Builders<MediaJob>.Filter.Nin(x => x.Id, deferred);
                        var job = await ClaimAsync(pending, now, stoppingToken);
                        if (job is not null)
                        {
                            running.Add(ExecuteJobAsync(job, stoppingToken));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("任务调度等待恢复：{Message}", error.Message);
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
        await Task.WhenAll(running);
    }

    private async Task<MediaJob?> ClaimAsync(FilterDefinition<MediaJob> pending, DateTime now, CancellationToken cancellationToken)
    {
        var jobs = context.Collection<MediaJob>("jobs");
        var candidate = await jobs.Find(pending).SortBy(value => value.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }
        await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, candidate.Id, cancellationToken);
        if (clipping is not null && await clipping.HasPreferredCapacityAsync(candidate, cancellationToken))
        {
            return null;
        }
        return await jobs.FindOneAndUpdateAsync(pending & Builders<MediaJob>.Filter.Eq(value => value.Id, candidate.Id),
            Builders<MediaJob>.Update.Set(value => value.State, "Running").Set(value => value.LeaseOwner, workerId)
                .Set(value => value.LeaseUntilUtc, now.AddSeconds(90)).Set(value => value.RemoteWorkerId, null)
                .Set(value => value.RemoteLeaseTokenHash, null).Set(value => value.RemoteAssignment, null).Set(value => value.RemoteSources, [])
                .Set(value => value.RemoteOutputSha256, null).Inc(value => value.Attempt, 1),
            new FindOneAndUpdateOptions<MediaJob> { ReturnDocument = ReturnDocument.After }, cancellationToken);
    }

    private async Task ExecuteJobAsync(MediaJob job, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(job, cancellation);
        string? jobRoot = null;
        BroadcastSession? session = null;
        var completed = false;
        try
        {
            if (job.Kind == "Repair" && job.MutationKey is null)
            {
                var claimed = await context.Collection<MediaJob>("jobs").UpdateOneAsync(Fence(job), Builders<MediaJob>.Update.Set(x => x.MutationKey, job.SessionId), cancellationToken: cancellation.Token);
                if (claimed.MatchedCount == 0)
                {
                    throw new OperationCanceledException("作业租约已失效。", cancellation.Token);
                }
                job.MutationKey = job.SessionId;
            }
            var settings = await settingsStore.GetAsync(cancellation.Token);
            session = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == job.SessionId).FirstOrDefaultAsync(cancellation.Token)
                      ?? throw new InfrastructureException(MessageCode.JOB_SOURCE_SESSION_NOT_FOUND, 404);
            jobRoot = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "jobs", job.Id));
            Directory.CreateDirectory(jobRoot);
            var manifest = await RecoverOutputAsync(job, session, jobRoot, paths.RecordsDirectory, cancellation.Token)
                           ?? await ProcessAsync(job, session, jobRoot, cancellation.Token);
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, job.Id, cancellation.Token);
            await using var libraryLock = job.Kind is "Clip" or "Repair"
                ? await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellation.Token) : null;
            var publishing = await context.Collection<MediaJob>("jobs").UpdateOneAsync(Fence(job), Builders<MediaJob>.Update.Set(x => x.State, "Publishing"), cancellationToken: cancellation.Token);
            if (publishing.MatchedCount == 0)
            {
                throw new OperationCanceledException("作业租约已失效。", cancellation.Token);
            }
            await PublishAsync(job, session, manifest, settings, cancellation.Token);
            var changed = await context.Collection<MediaJob>("jobs").UpdateOneAsync(Fence(job), Builders<MediaJob>.Update
                .Set(x => x.State, "Completed").Set(x => x.Progress, 100).Set(x => x.Error, null).Set(x => x.ErrorCode, null).Set(x => x.ErrorArguments, []).Set(x => x.FinishedAtUtc, DateTime.UtcNow)
                .Set(x => x.OutputRelativePaths, manifest.Paths).Set(x => x.OutputRelativePath, manifest.Paths.FirstOrDefault())
                .Set(x => x.MutationKey, null).Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntilUtc, null), cancellationToken: cancellation.Token);
            completed = changed.MatchedCount > 0;
            if (completed)
            {
                if (job.Kind == "Clip")
                {
                    await new ClipPublicationService(context, paths).ActivateAsync(job, cancellation.Token);
                }
                await publisher.PublishAsync("job", job.Id, job.OwnerId, cancellation.Token);
            }
        }
        catch (Exception error)
        {
            try
            {
                await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, job.Id);
                var current = await context.Collection<MediaJob>("jobs").Find(x => x.Id == job.Id).FirstOrDefaultAsync(CancellationToken.None);
                var state = current?.CancelRequested == true ? "Cancelled" : stoppingToken.IsCancellationRequested ? "Queued" : "Failed";
                var errorCode = job.ErrorCode ?? (error as InfrastructureException)?.Code ?? MessageCode.JOB_PROCESSING_FAILED.ToString();
                var errorArguments = job.ErrorCode is not null ? job.ErrorArguments : error is InfrastructureException { Code: not null } businessError ? businessError.Arguments.ToList() : [error.Message];
                var changed = await context.Collection<MediaJob>("jobs").UpdateOneAsync(Fence(job, true), Builders<MediaJob>.Update
                    .Set(x => x.State, state).Set(x => x.Error, state == "Failed" ? job.Error ?? error.Message : null)
                    .Set(x => x.ErrorCode, state == "Failed" ? errorCode : null).Set(x => x.ErrorArguments, state == "Failed" ? errorArguments : [])
                    .Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntilUtc, null)
                    .Set(x => x.MutationKey, state == "Queued" && job.Kind == "Repair" ? job.SessionId : null), cancellationToken: CancellationToken.None);
                if (changed.MatchedCount > 0)
                {
                    await publisher.PublishAsync("job", job.Id, job.OwnerId);
                }
            }
            catch (Exception databaseError)
            {
                logger.LogWarning("任务 {JobId} 将由过期租约恢复：{Message}", job.Id, databaseError.Message);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
            if (jobRoot is not null)
            {
                try
                {
                    await CleanupWorkAsync(job, session, jobRoot, completed);
                }
                catch (Exception error)
                {
                    logger.LogWarning("任务 {JobId} 的工作目录将稍后清理：{Message}", job.Id, error.Message);
                }
            }
        }
    }

    private async Task<JobOutputManifest> ProcessAsync(MediaJob job, BroadcastSession session, string jobRoot, CancellationToken cancellationToken)
    {
        var attemptDirectory = Path.Combine(jobRoot, job.Attempt.ToString());
        Directory.CreateDirectory(attemptDirectory);
        var manifest = JobOutputManifest.Create(job);
        var sessionDirectory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        var outputDirectory = MediaJobFiles.OutputDirectory(job, session, paths.RecordsDirectory, job.Attempt);
        Directory.CreateDirectory(outputDirectory);
        var progress = new JobProgressReporter(context, publisher, job, workerId);
        if (job.Kind == "Repair")
        {
            for (var index = 0; index < job.SourceSegments.Count; index++)
            {
                var segment = job.SourceSegments[index];
                var source = MediaPaths.ResolveSegment(paths.RecordsDirectory, session.DirectoryPath, segment);
                var destination = Path.Combine(outputDirectory, MediaPaths.SafeName(job.Title, 40) + "_" + (index + 1).ToString("D3") + ".flv");
                var segmentProgress = new JobProgressReporter(context, publisher, job, workerId, (double)index / job.SourceSegments.Count, (double)(index + 1) / job.SourceSegments.Count);
                var repaired = await processor.RepairAsync(source, destination, segmentProgress, cancellationToken);
                if (repaired.Outputs.Count == 0)
                {
                    throw new InvalidDataException("修复未返回媒体产物。");
                }
                foreach (var output in repaired.Outputs)
                {
                    if (!double.IsFinite(output.DurationMs) || output.DurationMs <= 0 || !double.IsFinite(output.SourceStartMs) || output.SourceStartMs < 0)
                    {
                        throw new InvalidDataException("修复产物时间范围无效。");
                    }
                    var path = Path.GetFullPath(output.Path);
                    RequireOutputPath(path, outputDirectory);
                    RequireOutputPath(Path.GetFullPath(output.IndexPath), outputDirectory);
                    var probe = await ValidateMediaAsync(path, cancellationToken);
                    if (Math.Abs(probe.DurationMs - output.DurationMs) > Math.Max(150, probe.FrameRate > 0 ? 2000 / probe.FrameRate : 150))
                    {
                        throw new InvalidDataException("修复产物的媒体时长与时间映射不符。");
                    }
                    if (!File.Exists(output.IndexPath) || new FileInfo(output.IndexPath).Length == 0)
                    {
                        throw new InvalidDataException("修复产物缺少有效索引。");
                    }
                    var relative = Path.GetRelativePath(paths.RecordsDirectory, path);
                    manifest.Paths.Add(relative);
                    manifest.ArtifactLengths.Add(relative, new FileInfo(path).Length);
                    manifest.ArtifactLengths.Add(Path.GetRelativePath(paths.RecordsDirectory, output.IndexPath), new FileInfo(output.IndexPath).Length);
                    var repairedId = job.Id + "_v" + manifest.Attempt + "_" + manifest.RepairedSegments.Count;
                    manifest.RepairedAssetSources[repairedId] = TranscodeAssetRegistry.AssetId(session, segment);
                    manifest.RepairedSegments.Add(new()
                    {
                        Id = repairedId,
                        SessionId = session.Id,
                        AssetId = repairedId,
                        AttemptId = job.Id,
                        RelativePath = Path.GetRelativePath(sessionDirectory, path),
                        IndexRelativePath = Path.GetRelativePath(sessionDirectory, output.IndexPath),
                        StartOffsetMs = segment.StartOffsetMs + output.SourceStartMs,
                        DurationMs = output.DurationMs,
                        CommittedBytes = new FileInfo(path).Length,
                        IsClosed = true,
                        Revision = job.SourceRevision + 1,
                        Codec = probe.VideoCodec == "hevc" ? "hevc" : "avc"
                    });
                }
                await segmentProgress.FlushAsync(cancellationToken);
            }
        }
        else
        {
            var output = Path.Combine(outputDirectory, MediaPaths.SafeName(job.Title, 40) + ".mp4");
            var temporary = Path.Combine(attemptDirectory, "output.mp4");
            var inputs = SessionTimeline.ExportSegmentSpans(job.SourceSegments, job.StartMs, job.EndMs, segment => MediaPaths.ResolveSegment(paths.RecordsDirectory, session.DirectoryPath, segment));
            var result = await processor.ExportAsync(new() { Inputs = inputs, OutputPath = temporary, Mode = job.Mode, StartMs = job.StartMs, DurationMs = job.EndMs - job.StartMs }, progress, cancellationToken);
            await ValidateMediaAsync(temporary, cancellationToken);
            await RequireLeaseAsync(job, cancellationToken);
            File.Move(temporary, output, false);
            var relative = Path.GetRelativePath(paths.RecordsDirectory, output);
            manifest.Paths.Add(relative);
            manifest.ArtifactLengths.Add(relative, new FileInfo(output).Length);
            manifest.ActualStartMs = result.ActualStartMs;
            manifest.ActualEndMs = result.ActualEndMs;
        }
        await progress.FlushAsync(cancellationToken);
        await RequireLeaseAsync(job, cancellationToken);
        await JsonFiles.WriteAsync(Path.Combine(attemptDirectory, "completed.json"), manifest, cancellationToken);
        return manifest;
    }

    private async Task PublishAsync(MediaJob job, BroadcastSession session, JobOutputManifest manifest, ServerSettings settings, CancellationToken cancellationToken)
    {
        if (job.Kind == "Clip")
        {
            await new ClipPublicationService(context, paths).PublishAsync(job, session, manifest, settings,
                token => RequireLeaseAsync(job, token), cancellationToken);
            return;
        }
        var sessionDirectory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        await using var publicationLock = await MediaJobFiles.AcquirePublicationLockAsync(sessionDirectory, cancellationToken);
        var lease = await RequireLeaseAsync(job, cancellationToken);
        if (job.Kind == "Repair")
        {
            var affectedAssets = job.SourceSegments.Select(segment => TranscodeAssetRegistry.AssetId(session, segment)).ToArray();
            await new TranscodeLifecycle(context, paths).MarkDirtyAsync(affectedAssets, cancellationToken);
            session = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == job.SessionId).FirstAsync(cancellationToken);
            if (session.MediaRevision == job.SourceRevision + 1)
            {
                if (session.MediaRevisionJobId != job.Id || session.MediaRevisionAttempt != manifest.Attempt)
                {
                    throw new InfrastructureException(MessageCode.JOB_REVISION_OWNED_ELSEWHERE, 409);
                }
                var published = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == session.Id && x.Revision == session.MediaRevision).ToListAsync(cancellationToken);
                if (manifest.RepairedSegments.Any(expected => !published.Any(actual => actual.Id == expected.Id && actual.RelativePath == expected.RelativePath
                        && actual.CommittedBytes == expected.CommittedBytes && actual.DurationMs == expected.DurationMs)))
                {
                    throw new InfrastructureException(MessageCode.JOB_ACTIVE_REVISION_INCOMPLETE, 409);
                }
                await RequireLeaseAsync(job, cancellationToken);
                await SessionManifestFiles.WriteAsync(Path.Combine(sessionDirectory, "场次.json"), new SessionManifest { Session = session, Segments = published }, cancellationToken);
                await new TranscodeLifecycle(context, paths).ReconcileUnderLibraryLockAsync(cancellationToken, affectedAssets);
                return;
            }
            if (session.MediaRevision != job.SourceRevision)
            {
                throw new InfrastructureException(MessageCode.JOB_SESSION_REVISION_CHANGED, 409);
            }
            var original = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == session.Id && x.Revision == job.SourceRevision).ToListAsync(cancellationToken);
            var next = original.Where(x => !job.SegmentIds.Contains(x.Id)).Select(item => new RecordingSegment
            {
                Id = job.Id + "_v" + manifest.Attempt + "_keep_" + item.Id,
                SessionId = item.SessionId,
                AssetId = item.AssetId,
                PendingTranscodeProfileIds = item.PendingTranscodeProfileIds.ToList(),
                AttemptId = item.AttemptId,
                RelativePath = item.RelativePath,
                IndexRelativePath = item.IndexRelativePath,
                RootRelativePath = item.RootRelativePath,
                RootIndexRelativePath = item.RootIndexRelativePath,
                StartOffsetMs = item.StartOffsetMs,
                DurationMs = item.DurationMs,
                CommittedBytes = item.CommittedBytes,
                IsClosed = item.IsClosed,
                Revision = job.SourceRevision + 1,
                Codec = item.Codec
            }).ToList();
            await InheritTranscodeIntentsAsync(job, session, manifest, settings, cancellationToken);
            next.AddRange(manifest.RepairedSegments);
            var segments = context.Collection<RecordingSegment>("segments");
            foreach (var item in next)
            {
                await RequireLeaseAsync(job, cancellationToken);
                try
                {
                    await segments.InsertOneAsync(new RecordingSegment { Id = item.Id, SessionId = item.SessionId, Revision = 0 }, cancellationToken: cancellationToken);
                }
                catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                }
                lease = await RequireLeaseAsync(job, cancellationToken);
                var changed = await segments.ReplaceOneAsync(LiveWrite(Builders<RecordingSegment>.Filter.Eq(x => x.Id, item.Id), lease), item, cancellationToken: cancellationToken);
                if (changed.MatchedCount == 0)
                {
                    throw new OperationCanceledException("修复发布执行权已失效。", cancellationToken);
                }
            }
            lease = await RequireLeaseAsync(job, cancellationToken);
            var ids = next.Select(x => x.Id).ToArray();
            await segments.DeleteManyAsync(LiveWrite(Builders<RecordingSegment>.Filter.Where(x => x.SessionId == session.Id && (x.Revision == 0 || x.Revision == job.SourceRevision + 1))
                & Builders<RecordingSegment>.Filter.Nin(x => x.Id, ids), lease), cancellationToken);
            lease = await RequireLeaseAsync(job, cancellationToken);
            var activated = await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(LiveWrite(Builders<BroadcastSession>.Filter.Where(x => x.Id == session.Id && x.MediaRevision == job.SourceRevision), lease),
                Builders<BroadcastSession>.Update.Set(x => x.MediaRevision, job.SourceRevision + 1).Set(x => x.MediaRevisionJobId, job.Id).Set(x => x.MediaRevisionAttempt, manifest.Attempt), cancellationToken: cancellationToken);
            if (activated.MatchedCount == 0)
            {
                throw new InfrastructureException(MessageCode.JOB_REVISION_COMMIT_CONFLICT, 409);
            }
            session.MediaRevision = job.SourceRevision + 1;
            session.MediaRevisionJobId = job.Id;
            session.MediaRevisionAttempt = manifest.Attempt;
            await RequireLeaseAsync(job, cancellationToken);
            await SessionManifestFiles.WriteAsync(Path.Combine(sessionDirectory, "场次.json"), new SessionManifest { Session = session, Segments = next }, cancellationToken);
            await new TranscodeLifecycle(context, paths).ReconcileUnderLibraryLockAsync(cancellationToken, affectedAssets);
        }
    }

    private async Task InheritTranscodeIntentsAsync(MediaJob job, BroadcastSession session, JobOutputManifest manifest,
        ServerSettings settings, CancellationToken cancellationToken)
    {
        var enabled = settings.Transcoding.Profiles.Where(x => x.Enabled).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var automatic = settings.Transcoding.Profiles.Where(x => session.AutoTranscodeEligible && x.Enabled && x.AutoTranscode)
            .Select(x => x.Id).ToArray();
        foreach (var segment in manifest.RepairedSegments)
        {
            segment.AssetId = TranscodeAssetRegistry.AssetId(session, segment);
            segment.PendingTranscodeProfileIds = automatic.ToList();
            if (!manifest.RepairedAssetSources.TryGetValue(segment.Id, out var assetId))
            {
                var source = job.SourceSegments.Where(x => x.StartOffsetMs <= segment.StartOffsetMs + 1
                    && x.StartOffsetMs + x.DurationMs > segment.StartOffsetMs).OrderByDescending(x => x.StartOffsetMs).FirstOrDefault();
                if (source is null)
                {
                    continue;
                }
                assetId = TranscodeAssetRegistry.AssetId(session, source);
            }
            var jobs = await context.Collection<TranscodeJob>("transcodeJobs")
                .Find(x => x.AssetId == assetId && !x.CancelRequested).ToListAsync(cancellationToken);
            var renditions = await context.Collection<PlaybackRendition>("playbackRenditions")
                .Find(x => x.AssetId == assetId && x.State == "Ready").ToListAsync(cancellationToken);
            segment.PendingTranscodeProfileIds = automatic.Concat(jobs.Select(x => x.Profile.Id)).Concat(renditions.Select(x => x.ProfileId))
                .Where(enabled.Contains).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    private async Task<JobOutputManifest?> RecoverOutputAsync(MediaJob job, BroadcastSession session, string directory, string root, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "completed.json", SearchOption.AllDirectories)
                     .OrderByDescending(path => int.TryParse(Path.GetFileName(Path.GetDirectoryName(path)), out var value) ? value : 0))
        {
            try
            {
                await using var input = File.OpenRead(file);
                var manifest = await JsonSerializer.DeserializeAsync<JobOutputManifest>(input, JsonFiles.Options, cancellationToken);
                var identity = JobOutputManifest.Create(job);
                if (manifest is null || manifest.SchemaVersion != 1 || manifest.JobId != job.Id || manifest.Kind != job.Kind
                    || manifest.SourceRevision != job.SourceRevision || manifest.InputFingerprint != identity.InputFingerprint
                    || manifest.Attempt <= 0 || manifest.Attempt > job.Attempt || manifest.Paths.Count == 0 || manifest.Paths.Distinct().Count() != manifest.Paths.Count)
                {
                    continue;
                }
                var outputDirectory = MediaJobFiles.OutputDirectory(job, session, root, manifest.Attempt);
                foreach (var artifact in manifest.ArtifactLengths)
                {
                    var path = MediaPaths.Resolve(root, artifact.Key);
                    RequireOutputPath(path, outputDirectory);
                    if (!File.Exists(path) || artifact.Value <= 0 || new FileInfo(path).Length != artifact.Value)
                    {
                        throw new InvalidDataException("恢复产物长度不符。");
                    }
                }
                foreach (var relative in manifest.Paths)
                {
                    var path = MediaPaths.Resolve(root, relative);
                    RequireOutputPath(path, outputDirectory);
                    if (!manifest.ArtifactLengths.ContainsKey(relative))
                    {
                        throw new InvalidDataException("恢复产物缺少完成校验信息。");
                    }
                    await ValidateMediaAsync(path, cancellationToken);
                }
                if (job.Kind == "Repair")
                {
                    if (manifest.RepairedSegments.Count != manifest.Paths.Count)
                    {
                        throw new InvalidDataException("修复清单没有覆盖所有产物。");
                    }
                    foreach (var segment in manifest.RepairedSegments)
                    {
                        var media = MediaPaths.ResolveSegment(root, session.DirectoryPath, segment);
                        var index = MediaPaths.ResolveSegmentIndex(root, session.DirectoryPath, segment);
                        if (string.IsNullOrEmpty(index))
                        {
                            throw new InvalidDataException("修复清单缺少索引路径。");
                        }
                        RequireOutputPath(media, outputDirectory);
                        RequireOutputPath(index, outputDirectory);
                        var mediaRelative = Path.GetRelativePath(root, media);
                        if (segment.SessionId != session.Id || segment.Revision != job.SourceRevision + 1 || !double.IsFinite(segment.DurationMs) || segment.DurationMs <= 0
                            || !double.IsFinite(segment.StartOffsetMs) || segment.StartOffsetMs < 0
                            || !segment.Id.StartsWith(job.Id + "_v" + manifest.Attempt + "_", StringComparison.Ordinal)
                            || !manifest.Paths.Contains(mediaRelative) || !manifest.ArtifactLengths.TryGetValue(mediaRelative, out var bytes) || bytes != segment.CommittedBytes
                            || !manifest.ArtifactLengths.ContainsKey(Path.GetRelativePath(root, index)))
                        {
                            throw new InvalidDataException("修复索引身份不符。");
                        }
                    }
                }
                return manifest;
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException or InfrastructureException)
            {
                logger.LogWarning("任务 {JobId} 的旧完成清单不可复用，将重新处理：{Message}", job.Id, error.Message);
            }
        }
        return null;
    }

    private async Task<MediaProbe> ValidateMediaAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InfrastructureException(MessageCode.JOB_OUTPUT_EMPTY);
        }
        var probe = await processor.ProbeAsync(path, cancellationToken);
        if (!double.IsFinite(probe.DurationMs) || probe.DurationMs <= 0 || string.IsNullOrWhiteSpace(probe.VideoCodec) || probe.Width <= 0 || probe.Height <= 0)
        {
            throw new InfrastructureException(MessageCode.JOB_OUTPUT_INVALID);
        }
        return probe;
    }

    private async Task<MediaJob> RequireLeaseAsync(MediaJob job, CancellationToken cancellationToken)
    {
        return await context.Collection<MediaJob>("jobs").Find(Fence(job)).FirstOrDefaultAsync(cancellationToken)
               ?? throw new OperationCanceledException("作业租约或执行状态已经失效。", cancellationToken);
    }

    private async Task HeartbeatAsync(MediaJob job, CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                var settings = await settingsStore.GetAsync(cancellation.Token);
                if (await storage.GetFreeBytesAsync(cancellation.Token) <= StorageService.MediaThreshold(settings))
                {
                    job.ErrorCode = MessageCode.JOB_DISK_RESERVE_REQUIRED.ToString();
                    job.ErrorArguments = [];
                    job.Error = LocalizedText.Format(job.ErrorCode, "", culture: System.Globalization.CultureInfo.InvariantCulture);
                    await cancellation.CancelAsync();
                    return;
                }
                var current = await context.Collection<MediaJob>("jobs").Find(Fence(job, true)).FirstOrDefaultAsync(cancellation.Token);
                if (current is null || current.CancelRequested)
                {
                    await cancellation.CancelAsync();
                    return;
                }
                var renewed = await context.Collection<MediaJob>("jobs").UpdateOneAsync(Fence(job), Builders<MediaJob>.Update.Set(x => x.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(90)), cancellationToken: cancellation.Token);
                if (renewed.MatchedCount == 0)
                {
                    await cancellation.CancelAsync();
                    return;
                }
            }
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            await cancellation.CancelAsync();
        }
    }

    private async Task FinalizeExpiredCancellationsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var jobs = context.Collection<MediaJob>("jobs");
        var expired = Builders<MediaJob>.Filter.Where(x => x.CancelRequested && (x.State == "Running" || x.State == "Publishing")
            && (x.LeaseUntilUtc == null || x.LeaseUntilUtc < now));
        foreach (var job in await jobs.Find(expired).ToListAsync(cancellationToken))
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, job.Id, cancellationToken);
            await jobs.UpdateOneAsync(expired & Builders<MediaJob>.Filter.Eq(x => x.Id, job.Id),
                Builders<MediaJob>.Update.Set(x => x.State, "Cancelled").Set(x => x.Error, null).Set(x => x.ErrorCode, null).Set(x => x.ErrorArguments, [])
                    .Set(x => x.MutationKey, null).Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntilUtc, null).Set(x => x.FinishedAtUtc, now), cancellationToken: cancellationToken);
        }
    }

    private FilterDefinition<MediaJob> Fence(MediaJob job, bool allowCancellation = false)
    {
        var filter = Builders<MediaJob>.Filter.Where(x => x.Id == job.Id && x.LeaseOwner == workerId && x.Attempt == job.Attempt && (x.State == "Running" || x.State == "Publishing"))
                     & new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$LeaseUntilUtc", "$$NOW" }));
        return allowCancellation ? filter : filter & Builders<MediaJob>.Filter.Eq(x => x.CancelRequested, false);
    }

    private static FilterDefinition<T> LiveWrite<T>(FilterDefinition<T> filter, MediaJob lease)
    {
        return filter & new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { new BsonDateTime(lease.LeaseUntilUtc!.Value), "$$NOW" }));
    }

    private static void RequireOutputPath(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidDataException("产物路径不在当前作业版本目录中。");
        }
        MediaPaths.Resolve(directory, Path.GetRelativePath(directory, path));
    }

    private async Task CleanupTerminalWorkAsync(CancellationToken cancellationToken)
    {
        var root = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "jobs"));
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            var job = await context.Collection<MediaJob>("jobs").Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (job is null || job.State is not ("Completed" or "Failed" or "Cancelled") || job.LeaseOwner is not null)
            {
                continue;
            }
            var session = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == job.SessionId).FirstOrDefaultAsync(cancellationToken);
            if (session is not null)
            {
                await CleanupWorkAsync(job, session, directory, job.State == "Completed");
            }
        }
    }

    private async Task CleanupWorkAsync(MediaJob job, BroadcastSession? session, string directory, bool completed)
    {
        await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, job.Id);
        var current = await context.Collection<MediaJob>("jobs").Find(x => x.Id == job.Id).FirstOrDefaultAsync();
        if (current is null || current.Attempt != job.Attempt || current.LeaseOwner is not null || current.State is "Running" or "Publishing" or "Queued")
        {
            return;
        }
        if (current.State == "Completed" && current.Kind == "Clip")
        {
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, CancellationToken.None);
            await new ClipPublicationService(context, paths).ActivateAsync(current, CancellationToken.None);
        }
        else if (current.Kind == "Clip")
        {
            await context.Collection<Clip>("clips").DeleteOneAsync(value => value.JobId == current.Id
                && value.PublicationAttempt <= current.Attempt && value.PublicationPending, CancellationToken.None);
        }
        if (!Directory.Exists(directory))
        {
            return;
        }
        if (session is not null)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (job.SourceSegments.Any(segment => MediaPaths.ResolveSegment(paths.RecordsDirectory, session.DirectoryPath, segment)
                    .StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, comparison)))
            {
                return;
            }
        }
        if (completed)
        {
            Directory.Delete(directory, true);
            return;
        }
        foreach (var attemptDirectory in Directory.EnumerateDirectories(directory))
        {
            foreach (var file in Directory.EnumerateFiles(attemptDirectory, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) != "completed.json")
                {
                    File.Delete(file);
                }
            }
            foreach (var nested in Directory.EnumerateDirectories(attemptDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(nested).Any())
                {
                    Directory.Delete(nested);
                }
            }
            if (!Directory.EnumerateFileSystemEntries(attemptDirectory).Any())
            {
                Directory.Delete(attemptDirectory);
            }
        }
        if (session is not null && !File.Exists(Path.Combine(directory, job.Attempt.ToString(), "completed.json")))
        {
            var output = MediaJobFiles.OutputDirectory(job, session, paths.RecordsDirectory, job.Attempt);
            var sourcePaths = job.SourceSegments.Select(segment => MediaPaths.ResolveSegment(paths.RecordsDirectory, session.DirectoryPath, segment));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (Directory.Exists(output) && !sourcePaths.Any(path => path.StartsWith(Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar, comparison)))
            {
                Directory.Delete(output, true);
            }
        }
    }
}
