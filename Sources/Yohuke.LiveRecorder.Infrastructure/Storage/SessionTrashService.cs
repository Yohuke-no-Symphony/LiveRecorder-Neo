using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>以可恢复日志回收场次目录，并在清理前检查共享媒体和弹幕的引用。</summary>
public sealed class SessionTrashService(IMongoContext context, ISettingsStore settingsStore, IAppPaths paths)
{
    private const string JOURNAL_NAME = "session-entry.json";

    /// <summary>将已结束且没有活动作业的场次移入回收站。</summary>
    public async Task TrashAsync(string id, string ownerId, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        await TrashCoreAsync(id, ownerId, false, cancellationToken);
    }

    /// <summary>更新正常场次的永久保留标记，取消时重新计时，并同步保存可重建的场次清单。</summary>
    public async Task<BroadcastSession> SetPermanentAsync(string id, bool isPermanent, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var session = await context.Collection<BroadcastSession>("sessions")
                          .Find(x => x.Id == id && !x.IsTrashed && !x.IsHidden && x.ProjectionReady)
                          .FirstOrDefaultAsync(cancellationToken)
                      ?? throw new InfrastructureException(MessageCode.LIBRARY_SESSION_NOT_FOUND, 404);
        if (await context.Collection<TrashEntry>("trash").Find(x => x.EntityId == id && x.Kind == "Session")
                .AnyAsync(cancellationToken) || await FindJournalAsync(id, cancellationToken) is not null)
        {
            throw new InfrastructureException(MessageCode.STORAGE_OPERATION_IN_PROGRESS, 409);
        }

        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == id)
            .ToListAsync(cancellationToken);
        var persisted = await SessionManifestFiles.SetPermanentAsync(
            new() { Session = session, Segments = segments }, ResolveDirectory(session.DirectoryPath),
            isPermanent, cancellationToken);
        await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == id,
            Builders<BroadcastSession>.Update.Set(x => x.IsPermanent, persisted.IsPermanent)
                .Set(x => x.RetentionStartedAtUtc, persisted.RetentionStartedAtUtc),
            cancellationToken: cancellationToken);
        session.IsPermanent = persisted.IsPermanent;
        session.RetentionStartedAtUtc = persisted.RetentionStartedAtUtc;
        return session;
    }

    private async Task TrashCoreAsync(string id, string ownerId, bool expiredOnly,
        CancellationToken cancellationToken)
    {
        await SessionSegmentTrashFiles.EnsureNoPendingAsync(paths.RecordsDirectory, id, cancellationToken);
        await LiveMarkerProjectionGuard.EnsureWritableAsync(paths.RecordsDirectory, id, cancellationToken);
        var existing = await context.Collection<TrashEntry>("trash").Find(x => x.EntityId == id && x.Kind == "Session")
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.State is "Pending" or "Moving")
            {
                await CompleteMoveAsync(await ReadJournalAsync(existing.Id, cancellationToken), cancellationToken);
            }
            else if (existing.State != "Active")
            {
                throw new InfrastructureException(MessageCode.STORAGE_OPERATION_IN_PROGRESS, 409);
            }

            return;
        }

        var interrupted = await FindJournalAsync(id, cancellationToken);
        if (interrupted is not null)
        {
            if (interrupted.Entry.State is "Restoring" or "Restored")
            {
                await CompleteRestoreAsync(interrupted, cancellationToken);
            }
            else if (interrupted.Entry.State == "Purging")
            {
                await CompletePurgeAsync(interrupted, cancellationToken);
            }
            else
            {
                await CompleteMoveAsync(interrupted, cancellationToken);
                return;
            }
        }

        var session = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == id)
                          .FirstOrDefaultAsync(cancellationToken)
                      ?? throw new InfrastructureException(MessageCode.LIBRARY_SESSION_NOT_FOUND, 404);
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (expiredOnly)
        {
            await SynchronizeRetentionAsync(session, cancellationToken);
            if (!IsExpired(session, settings, DateTime.UtcNow))
            {
                return;
            }
        }

        if (session.IsHidden || !session.ProjectionReady)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_DELETE_NOT_EDITABLE, 409);
        }

        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == id)
            .ToListAsync(cancellationToken);
        if (session.EndedAtUtc is null || session.State == "Recording" || segments.Any(x => !x.IsClosed))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_DELETE_RECORDING_ACTIVE, 409);
        }

        if (await HasActiveJobAsync(id, cancellationToken))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_DELETE_JOB_ACTIVE, 409);
        }

        var entry = new TrashEntry
        {
            Kind = "Session",
            OwnerId = ownerId,
            EntityId = session.Id,
            Title = session.Title,
            OriginalRelativePath = session.DirectoryPath,
            Bytes = session.TotalBytes > 0
                ? session.TotalBytes
                : segments.Where(x => x.Revision == session.MediaRevision).Sum(x => x.CommittedBytes),
            RetainedInPlace = await HasReferencesAsync(session.DirectoryPath, session.Id, cancellationToken)
        };
        entry.PurgeAtUtc = TrashRetentionPolicy.GetPurgeAtUtc(entry, settings);
        entry.TrashRelativePath = Path.Combine(".trash", entry.Id, "recording");
        var journal = new SessionTrashJournal
            { Entry = entry, Manifest = new() { Session = session, Segments = segments } };
        await new TranscodeLifecycle(context, paths).MarkDirtyAsync(
            segments.Select(segment => TranscodeAssetRegistry.AssetId(session, segment)).ToArray(), cancellationToken);
        await context.Collection<TrashEntry>("trash").Indexes.CreateOneAsync(new CreateIndexModel<TrashEntry>(
                Builders<TrashEntry>.IndexKeys.Ascending(x => x.EntityId),
                new CreateIndexOptions { Name = "trash_entity_unique", Unique = true }),
            cancellationToken: cancellationToken);
        await PersistAsync(journal, cancellationToken);
        await CompleteMoveAsync(journal, cancellationToken);
    }

    /// <summary>恢复当前回收意图，不让过期请求重新移动已恢复的目录。</summary>
    public async Task RestoreAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var current = await CurrentAsync(entry.Id, cancellationToken);
        if (current is null)
        {
            return;
        }

        var journal = await ReadJournalAsync(current.Id, cancellationToken);
        if (journal.Entry.State == "Purging")
        {
            throw new InfrastructureException(MessageCode.STORAGE_PURGING, 409);
        }

        if (journal.Entry.State is "Pending" or "Moving")
        {
            await CompleteMoveAsync(journal, cancellationToken);
        }

        await CompleteRestoreAsync(journal, cancellationToken);
    }

    /// <summary>删除回收场次及无人再使用的源档案，保留其他场次仍在引用的文件。</summary>
    public async Task PurgeAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var current = await CurrentAsync(entry.Id, cancellationToken);
        if (current is null)
        {
            return;
        }

        var journal = await ReadJournalAsync(current.Id, cancellationToken);
        if (journal.Entry.State is "Restoring" or "Restored")
        {
            await CompleteRestoreAsync(journal, cancellationToken);
            return;
        }

        if (journal.Entry.State is "Pending" or "Moving")
        {
            await CompleteMoveAsync(journal, cancellationToken);
        }

        await CompletePurgeAsync(journal, cancellationToken);
    }

    /// <summary>恢复磁盘日志中的中断操作，按保留期限清理场次及无引用源文件。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var trashRoot = MediaPaths.Resolve(paths.RecordsDirectory, ".trash");
        if (Directory.Exists(trashRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(trashRoot))
            {
                var journalPath = Path.Combine(directory, JOURNAL_NAME);
                if (!File.Exists(journalPath))
                {
                    continue;
                }

                try
                {
                    var journal = await ReadAsync<SessionTrashJournal>(journalPath, cancellationToken);
                    var settings = await settingsStore.GetAsync(cancellationToken);
                    var purgeAt = TrashRetentionPolicy.GetPurgeAtUtc(journal.Entry, settings);
                    if (journal.Entry.PurgeAtUtc != purgeAt)
                    {
                        journal.Entry.PurgeAtUtc = purgeAt;
                        await PersistAsync(journal, cancellationToken);
                    }

                    if (journal.Entry.State is "Restoring" or "Restored")
                    {
                        await CompleteRestoreAsync(journal, cancellationToken);
                    }
                    else if (journal.Entry.State == "Purging" || journal.Entry.PurgeAtUtc <= DateTime.UtcNow)
                    {
                        if (journal.Entry.State is "Pending" or "Moving")
                        {
                            await CompleteMoveAsync(journal, cancellationToken);
                        }

                        await CompletePurgeAsync(journal, cancellationToken);
                    }
                    else
                    {
                        await CompleteMoveAsync(journal, cancellationToken);
                    }
                }
                catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
                {
                }
            }
        }

        var retentionSettings = await settingsStore.GetAsync(cancellationToken);
        if (retentionSettings.RecordingRetentionDays > 0)
        {
            var deadline = DateTime.UtcNow.AddDays(-retentionSettings.RecordingRetentionDays);
            var expired = await context.Collection<BroadcastSession>("sessions")
                .Find(Builders<BroadcastSession>.Filter.Where(x => !x.IsTrashed && !x.IsHidden && x.ProjectionReady &&
                           x.EndedAtUtc != null && x.EndedAtUtc <= deadline &&
                           (x.RetentionStartedAtUtc == null || x.RetentionStartedAtUtc <= deadline)) &
                      Builders<BroadcastSession>.Filter.Ne(x => x.IsPermanent, true))
                .ToListAsync(cancellationToken);
            foreach (var session in expired)
            {
                try
                {
                    await TrashCoreAsync(session.Id, "", true, cancellationToken);
                }
                catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
                {
                }
            }
        }

        await CollectHiddenSourcesAsync(cancellationToken);
        await CollectRetiredSourcesAsync(cancellationToken);
    }

    private async Task SynchronizeRetentionAsync(BroadcastSession session, CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResolveDirectory(session.DirectoryPath), "场次.json");
        var persisted = await SessionManifestFiles.ReadSessionAsync(path, cancellationToken);
        if (persisted is null)
        {
            return;
        }

        if (persisted.Id != session.Id)
        {
            throw new InvalidDataException("Session manifest identity does not match the recording.");
        }

        if (session.IsPermanent == persisted.IsPermanent &&
            session.RetentionStartedAtUtc == persisted.RetentionStartedAtUtc)
        {
            return;
        }

        session.IsPermanent = persisted.IsPermanent;
        session.RetentionStartedAtUtc = persisted.RetentionStartedAtUtc;
        await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.IsPermanent, session.IsPermanent)
                .Set(x => x.RetentionStartedAtUtc, session.RetentionStartedAtUtc),
            cancellationToken: cancellationToken);
    }

    private static bool IsExpired(BroadcastSession session, ServerSettings settings, DateTime now)
    {
        return SessionRetentionPolicy.IsExpired(session, settings, now);
    }

    /// <summary>启动重建只扫描正式录播目录，不把回收目录和操作日志作为新录播导入。</summary>
    public static bool CanReconcile(string recordsRoot, string manifestPath)
    {
        var relative = Path.GetRelativePath(recordsRoot, manifestPath);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is not (".trash" or ".work");
    }

    private async Task CompleteMoveAsync(SessionTrashJournal journal, CancellationToken cancellationToken)
    {
        var entry = journal.Entry;
        await LiveMarkerProjectionGuard.EnsureWritableAsync(paths.RecordsDirectory, entry.EntityId, cancellationToken);
        var source = ResolveDirectory(entry.OriginalRelativePath);
        var destination = ResolveDirectory(entry.TrashRelativePath);
        if (entry.State is "Pending" or "Moving")
        {
            if (await HasActiveJobAsync(entry.EntityId, cancellationToken))
            {
                throw new InfrastructureException(MessageCode.LIBRARY_DELETE_JOB_ACTIVE, 409);
            }

            var latest = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == entry.EntityId)
                .FirstOrDefaultAsync(cancellationToken);
            if (latest is { IsTrashed: false })
            {
                if (latest.IsHidden || !latest.ProjectionReady)
                {
                    throw new InfrastructureException(MessageCode.LIBRARY_DELETE_NOT_EDITABLE, 409);
                }

                var segments = await context.Collection<RecordingSegment>("segments")
                    .Find(x => x.SessionId == latest.Id)
                    .ToListAsync(cancellationToken);
                if (latest.EndedAtUtc is null || latest.State == "Recording" || segments.Any(x => !x.IsClosed))
                {
                    throw new InfrastructureException(MessageCode.LIBRARY_DELETE_RECORDING_ACTIVE, 409);
                }

                journal.Manifest = new() { Session = latest, Segments = segments };
            }

            if (Directory.Exists(source))
            {
                entry.RetainedInPlace |=
                    await HasReferencesAsync(entry.OriginalRelativePath, entry.EntityId, cancellationToken);
            }
        }

        if (!entry.RetainedInPlace && Directory.Exists(source) && Directory.Exists(destination))
        {
            throw new InfrastructureException(MessageCode.STORAGE_TRASH_PATH_CONFLICT, 409);
        }

        if (entry.State != "Active")
        {
            entry.State = "Moving";
            await PersistAsync(journal, cancellationToken);
        }

        journal.Manifest.Session.IsTrashed = true;
        await context.Collection<RecordingSessionReceipt>("recordingSessions").UpdateOneAsync(value => value.Id == journal.Manifest.Session.Id,
            Builders<RecordingSessionReceipt>.Update.Set(value => value.Retired, true), cancellationToken: cancellationToken);
        var actual = entry.RetainedInPlace || Directory.Exists(source) ? source : destination;
        await SaveManifestAsync(journal.Manifest, actual, true, cancellationToken);
        if (!entry.RetainedInPlace && Directory.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
        }

        entry.State = "Active";
        await PersistAsync(journal, cancellationToken);
        await new TranscodeLifecycle(context, paths).ReconcileUnderLibraryLockAsync(cancellationToken,
            journal.Manifest.Segments.Select(segment => TranscodeAssetRegistry.AssetId(journal.Manifest.Session, segment)).ToArray());
    }

    private async Task CompleteRestoreAsync(SessionTrashJournal journal, CancellationToken cancellationToken)
    {
        await new TranscodeLifecycle(context, paths).MarkDirtyAsync(
            journal.Manifest.Segments.Select(segment => TranscodeAssetRegistry.AssetId(journal.Manifest.Session, segment)).ToArray(), cancellationToken);
        var entry = journal.Entry;
        if (entry.State == "Restored")
        {
            await FinishAsync(journal, cancellationToken);
            return;
        }

        var source = ResolveDirectory(entry.TrashRelativePath);
        var destination = ResolveDirectory(entry.OriginalRelativePath);
        if (!entry.RetainedInPlace)
        {
            if (Directory.Exists(destination) && (entry.State != "Restoring" || Directory.Exists(source)))
            {
                throw new InfrastructureException(MessageCode.STORAGE_RESTORE_DESTINATION_EXISTS, 409);
            }

            if (!Directory.Exists(source) && !Directory.Exists(destination))
            {
                throw new InfrastructureException(MessageCode.STORAGE_TRASH_FILE_MISSING, 410);
            }
        }

        if (entry.State != "Restoring" || journal.Manifest.Session.RetentionStartedAtUtc is null)
        {
            journal.Manifest.Session.RetentionStartedAtUtc = DateTime.UtcNow;
        }

        entry.State = "Restoring";
        await PersistAsync(journal, cancellationToken);
        if (!entry.RetainedInPlace && Directory.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
        }

        journal.Manifest.Session.IsTrashed = false;
        await context.Collection<RecordingSessionReceipt>("recordingSessions").UpdateOneAsync(value => value.Id == journal.Manifest.Session.Id,
            Builders<RecordingSessionReceipt>.Update.Set(value => value.Retired, false), cancellationToken: cancellationToken);
        await SaveManifestAsync(journal.Manifest, destination, false, cancellationToken);
        entry.State = "Restored";
        await PersistAsync(journal, cancellationToken);
        await FinishAsync(journal, cancellationToken);
    }

    private async Task CompletePurgeAsync(SessionTrashJournal journal, CancellationToken cancellationToken)
    {
        await new TranscodeLifecycle(context, paths).MarkDirtyAsync(
            journal.Manifest.Segments.Select(segment => TranscodeAssetRegistry.AssetId(journal.Manifest.Session, segment)).ToArray(), cancellationToken);
        await SessionSegmentTrashFiles.EnsureNoEntriesAsync(paths.RecordsDirectory, journal.Entry.EntityId,
            cancellationToken);
        journal.Entry.State = "Purging";
        await PersistAsync(journal, cancellationToken);
        var directory = journal.Entry.RetainedInPlace
            ? journal.Entry.OriginalRelativePath
            : journal.Entry.TrashRelativePath;
        await RetireManifestAsync(journal.Manifest, directory, cancellationToken);
        await CollectHiddenSourcesAsync(cancellationToken);
        await CollectRetiredSourcesAsync(cancellationToken);
        await FinishAsync(journal, cancellationToken);
    }

    private async Task RetireManifestAsync(SessionManifest manifest, string actualDirectory,
        CancellationToken cancellationToken)
    {
        var session = manifest.Session;
        var actual = ResolveDirectory(actualDirectory);
        new LiveMarkerFileStore(paths).DeleteAll(actualDirectory);
        if (actualDirectory == session.DirectoryPath &&
            await HasReferencesAsync(session.DirectoryPath, session.Id, cancellationToken))
        {
            await JsonFiles.WriteAsync(RetiredPath(session.Id), manifest, cancellationToken);
            File.Delete(Path.Combine(actual, "场次.json"));
        }
        else if (Directory.Exists(actual))
        {
            Directory.Delete(actual, true);
        }

        await context.Collection<RecordingSegment>("segments")
            .DeleteManyAsync(x => x.SessionId == session.Id, cancellationToken);
        await context.Collection<SessionBookmark>("sessionBookmarks")
            .DeleteManyAsync(x => x.SessionId == session.Id, cancellationToken);
        await context.Collection<BroadcastSession>("sessions")
            .DeleteOneAsync(x => x.Id == session.Id, cancellationToken);
    }

    private async Task CollectHiddenSourcesAsync(CancellationToken cancellationToken)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var sessions = await context.Collection<BroadcastSession>("sessions")
                .Find(FilterDefinition<BroadcastSession>.Empty).ToListAsync(cancellationToken);
            var referenced = sessions.SelectMany(x => x.GroupedSessionIds).ToHashSet(StringComparer.Ordinal);
            foreach (var session in sessions.Where(x =>
                         x.IsHidden && !x.IsTrashed && x.ProjectionReady && x.EndedAtUtc != null &&
                         !referenced.Contains(x.Id)))
            {
                if (await HasActiveJobAsync(session.Id, cancellationToken))
                {
                    continue;
                }

                var segments = await context.Collection<RecordingSegment>("segments")
                    .Find(x => x.SessionId == session.Id).ToListAsync(cancellationToken);
                if (segments.Any(x => !x.IsClosed))
                {
                    continue;
                }

                await RetireManifestAsync(new() { Session = session, Segments = segments }, session.DirectoryPath,
                    cancellationToken);
                changed = true;
            }
        }
    }

    private async Task CollectRetiredSourcesAsync(CancellationToken cancellationToken)
    {
        var root = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".trash", ".sources"));
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var manifest = await ReadAsync<SessionManifest>(file, cancellationToken);
            if (await HasReferencesAsync(manifest.Session.DirectoryPath, null, cancellationToken))
            {
                if (!await HasReferencesAsync(manifest.Session.DirectoryPath, null, cancellationToken, false))
                {
                    await RemoveRetiredMediaAsync(manifest, cancellationToken);
                }

                continue;
            }

            var directory = ResolveDirectory(manifest.Session.DirectoryPath);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            File.Delete(file);
        }
    }

    private async Task<bool> HasReferencesAsync(string relativeDirectory, string? excludedId,
        CancellationToken cancellationToken, bool includeDerivedOutputs = true)
    {
        var directory = ResolveDirectory(relativeDirectory);
        if ((await SessionSegmentTrashFiles.RetainedPathsAsync(paths.RecordsDirectory, cancellationToken))
            .Any(relative => IsInside(relative, directory)))
        {
            return true;
        }
        var jobs = await context.Collection<MediaJob>("jobs")
            .Find(x => x.State == "Queued" || x.State == "Running" || x.State == "Publishing" || x.State == "Completed")
            .ToListAsync(cancellationToken);
        if (includeDerivedOutputs)
        {
            var clips = await context.Collection<Clip>("clips").Find(x => !x.IsTrashed || x.PublicationPending).ToListAsync(cancellationToken);
            var clipIntents = await context.Collection<TrashEntry>("trash").Find(x => x.Kind == "Clip" &&
                (x.State == "Pending" || x.State == "Moving" || x.State == "Restoring")).ToListAsync(cancellationToken);
            if (clips.Any(x => IsInside(x.RelativePath, directory)) ||
                clipIntents.Any(x => IsInside(x.OriginalRelativePath, directory)) ||
                jobs.Any(x => x.State == "Completed" && x.Kind != "Clip" &&
                              x.OutputRelativePaths.Any(output => IsInside(output, directory))))
            {
                return true;
            }
        }

        var sessions = await context.Collection<BroadcastSession>("sessions")
            .Find(FilterDefinition<BroadcastSession>.Empty).ToListAsync(cancellationToken);
        var diskManifests = new List<SessionManifest>();
        foreach (var file in Directory.EnumerateFiles(paths.RecordsDirectory, "场次.json",
                     new EnumerationOptions
                         { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (CanReconcile(paths.RecordsDirectory, file))
            {
                diskManifests.Add(await ReadAsync<SessionManifest>(file, cancellationToken));
            }
        }

        sessions.AddRange(diskManifests.Select(x => x.Session));
        var others = sessions.Where(x => x.Id != excludedId).ToList();
        if (others.Any(x => IsInside(x.DirectoryPath, directory) ||
                            x.CoverRelativePath is { } cover && IsInside(cover, directory) ||
                            x.EventSources.Any(source => IsInside(source.DirectoryPath, directory))))
        {
            return true;
        }

        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId != excludedId)
            .ToListAsync(cancellationToken);
        segments.AddRange(diskManifests.Where(x => x.Session.Id != excludedId).SelectMany(x => x.Segments));
        if (segments.Any(x => x.RootRelativePath is { } media && IsInside(media, directory) ||
                              x.RootIndexRelativePath is { } index && IsInside(index, directory)))
        {
            return true;
        }

        foreach (var job in jobs.Where(x => x.State != "Completed"))
        {
            var owner = sessions.FirstOrDefault(x => x.Id == job.SessionId);
            if (owner is not null && IsInside(owner.DirectoryPath, directory) ||
                job.SourceSegments.Any(x => x.RootRelativePath is { } media && IsInside(media, directory) ||
                                            x.RootIndexRelativePath is { } index && IsInside(index, directory)))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInside(string relative, string directory)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return false;
        }

        var candidate = MediaPaths.Resolve(paths.RecordsDirectory, relative);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(candidate, directory, comparison) ||
               candidate.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                   comparison);
    }

    private string ResolveDirectory(string relative)
    {
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, relative);
        if (Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null)
        {
            throw new InfrastructureException(MessageCode.PATH_MEDIA_DIRECTORY_SYMLINK);
        }

        return directory;
    }

    private async Task RemoveRetiredMediaAsync(SessionManifest manifest, CancellationToken cancellationToken)
    {
        var directory = ResolveDirectory(manifest.Session.DirectoryPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var keep = new HashSet<string>(comparison);
        foreach (var relative in await SessionSegmentTrashFiles.RetainedPathsAsync(paths.RecordsDirectory, cancellationToken))
        {
            if (IsInside(relative, directory))
            {
                keep.Add(MediaPaths.Resolve(paths.RecordsDirectory, relative));
            }
        }
        var jobs = await context.Collection<MediaJob>("jobs").Find(x => x.State == "Completed" && x.Kind != "Clip")
            .ToListAsync(cancellationToken);
        var clips = await context.Collection<Clip>("clips").Find(x => !x.IsTrashed || x.PublicationPending).ToListAsync(cancellationToken);
        var intents = await context.Collection<TrashEntry>("trash").Find(x => x.Kind == "Clip" &&
                                                                              (x.State == "Pending" ||
                                                                               x.State == "Moving" ||
                                                                               x.State == "Restoring"))
            .ToListAsync(cancellationToken);
        foreach (var relative in jobs.SelectMany(x => x.OutputRelativePaths).Concat(clips.Select(x => x.RelativePath))
                     .Concat(intents.Select(x => x.OriginalRelativePath)))
        {
            if (IsInside(relative, directory))
            {
                keep.Add(MediaPaths.Resolve(paths.RecordsDirectory, relative));
            }
        }

        foreach (var segment in manifest.Segments)
        {
            var relativeMedia = segment.RootRelativePath ??
                                Path.Combine(manifest.Session.DirectoryPath, segment.RelativePath);
            var relativeIndex = segment.RootIndexRelativePath ??
                                (string.IsNullOrEmpty(segment.IndexRelativePath)
                                    ? null
                                    : Path.Combine(manifest.Session.DirectoryPath, segment.IndexRelativePath));
            foreach (var relative in new[] { relativeMedia, relativeIndex })
            {
                if (string.IsNullOrEmpty(relative) || !IsInside(relative, directory))
                {
                    continue;
                }

                var file = MediaPaths.Resolve(paths.RecordsDirectory, relative);
                if (!keep.Contains(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    private async Task SaveManifestAsync(SessionManifest manifest, string directory, bool preserveRetention,
        CancellationToken cancellationToken)
    {
        foreach (var segment in manifest.Segments)
        {
            segment.AssetId = TranscodeAssetRegistry.AssetId(manifest.Session, segment);
        }
        await SessionManifestFiles.WriteAsync(Path.Combine(directory, "场次.json"), manifest, preserveRetention,
            cancellationToken);
        await context.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == manifest.Session.Id,
            manifest.Session,
            new ReplaceOptions { IsUpsert = true }, cancellationToken);
        foreach (var segment in manifest.Segments)
        {
            await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id, segment,
                new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
    }

    private async Task PersistAsync(SessionTrashJournal journal, CancellationToken cancellationToken)
    {
        await JsonFiles.WriteAsync(JournalPath(journal.Entry.Id), journal, cancellationToken);
        await context.Collection<TrashEntry>("trash").ReplaceOneAsync(x => x.Id == journal.Entry.Id, journal.Entry,
            new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    private async Task FinishAsync(SessionTrashJournal journal, CancellationToken cancellationToken)
    {
        await context.Collection<TrashEntry>("trash")
            .DeleteOneAsync(x => x.Id == journal.Entry.Id && x.Kind == "Session", cancellationToken);
        var directory = Path.GetDirectoryName(JournalPath(journal.Entry.Id))!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
        await new TranscodeLifecycle(context, paths).ReconcileUnderLibraryLockAsync(cancellationToken,
            journal.Manifest.Segments.Select(segment => TranscodeAssetRegistry.AssetId(journal.Manifest.Session, segment)).ToArray());
    }

    private Task<bool> HasActiveJobAsync(string sessionId, CancellationToken cancellationToken)
    {
        return context.Collection<MediaJob>("jobs").Find(x => x.SessionId == sessionId &&
                                                              (x.State == "Queued" || x.State == "Running" ||
                                                               x.State == "Publishing")).AnyAsync(cancellationToken);
    }

    private async Task<TrashEntry?> CurrentAsync(string id, CancellationToken cancellationToken)
    {
        return await context.Collection<TrashEntry>("trash").Find(x => x.Id == id && x.Kind == "Session")
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<SessionTrashJournal?> FindJournalAsync(string sessionId, CancellationToken cancellationToken)
    {
        var root = MediaPaths.Resolve(paths.RecordsDirectory, ".trash");
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(directory, JOURNAL_NAME);
            if (!File.Exists(file))
            {
                continue;
            }

            var journal = await ReadAsync<SessionTrashJournal>(file, cancellationToken);
            if (journal.Entry.EntityId == sessionId)
            {
                return journal;
            }
        }

        return null;
    }

    private string JournalPath(string id) =>
        MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".trash", id, JOURNAL_NAME));

    private string RetiredPath(string id) =>
        MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".trash", ".sources", id + ".json"));

    private Task<SessionTrashJournal> ReadJournalAsync(string id, CancellationToken cancellationToken) =>
        ReadAsync<SessionTrashJournal>(JournalPath(id), cancellationToken);

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, true);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonFiles.Options, cancellationToken)
               ?? throw new InvalidDataException("Invalid session trash journal.");
    }
}
