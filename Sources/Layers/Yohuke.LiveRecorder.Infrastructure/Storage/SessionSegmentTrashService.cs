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

/// <summary>以持久日志回收和恢复独立录像分段，保护共享媒体及原始时间映射。</summary>
public sealed class SessionSegmentTrashService(IMongoContext context, ISettingsStore settingsStore, IAppPaths paths)
{
    /// <summary>从已结束场次移除一个分段，并至少保留一个有效分段。</summary>
    public async Task TrashAsync(string sessionId, string segmentId, string ownerId,
        CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var existing = (await SessionSegmentTrashFiles.ReadAllAsync(paths.RecordsDirectory, cancellationToken))
            .FirstOrDefault(x => x.Entry.SessionId == sessionId && x.Segment.Id == segmentId);
        if (existing is not null)
        {
            await ResumeAsync(existing, cancellationToken);
            return;
        }

        await SessionSegmentTrashFiles.EnsureNoPendingAsync(paths.RecordsDirectory, sessionId, cancellationToken);
        var session = await RequireSessionAsync(sessionId, cancellationToken);
        var segments = await SegmentsAsync(sessionId, cancellationToken);
        var current = segments.Where(x => x.Revision == session.MediaRevision).ToList();
        var segment = current.FirstOrDefault(x => x.Id == segmentId)
                      ?? throw new InfrastructureException(MessageCode.LIBRARY_SEGMENT_NOT_FOUND, 404);
        if (session.EndedAtUtc is null || session.State == "Recording" || segments.Any(x => !x.IsClosed))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_DELETE_RECORDING_ACTIVE, 409);
        }
        if (current.Count <= 1)
        {
            throw new InfrastructureException(MessageCode.SEGMENT_TRASH_LAST_SEGMENT, 409);
        }
        if (await context.Collection<MediaJob>("jobs")
                .Find(x => x.SessionId == sessionId && (x.State == "Queued" || x.State == "Running" || x.State == "Publishing"))
                .AnyAsync(cancellationToken))
        {
            throw new InfrastructureException(MessageCode.LIBRARY_DELETE_JOB_ACTIVE, 409);
        }

        var mediaPath = MediaPaths.ResolveSegment(paths.RecordsDirectory, session.DirectoryPath, segment);
        var indexPath = MediaPaths.ResolveSegmentIndex(paths.RecordsDirectory, session.DirectoryPath, segment);
        var references = await new MediaFileReferenceService(context, paths)
            .GetReferencedFilesAsync(segment.Id, null, cancellationToken);
        var entry = new TrashEntry
        {
            Kind = "Segment",
            EntityId = segment.Id,
            SessionId = sessionId,
            OwnerId = ownerId,
            Title = session.Title + " · " + TimeSpan.FromMilliseconds(segment.StartOffsetMs).ToString("c"),
            OriginalRelativePath = Path.GetRelativePath(paths.RecordsDirectory, mediaPath),
            Bytes = segment.CommittedBytes,
            RetainedInPlace = references.Contains(mediaPath) || references.Contains(indexPath)
        };
        entry.TrashRelativePath = Path.Combine(".trash", entry.Id, "segment", "media" + Path.GetExtension(mediaPath));
        entry.PurgeAtUtc = TrashRetentionPolicy.GetPurgeAtUtc(entry, await settingsStore.GetAsync(cancellationToken));
        var journal = new SessionSegmentTrashJournal
        {
            Entry = entry,
            Segment = segment,
            SessionDirectory = session.DirectoryPath,
            MediaRevision = session.MediaRevision,
            MediaRelativePath = entry.OriginalRelativePath,
            IndexRelativePath = File.Exists(indexPath) ? Path.GetRelativePath(paths.RecordsDirectory, indexPath) : null
        };
        await new TranscodeLifecycle(context, paths).MarkDirtyAsync([TranscodeAssetRegistry.AssetId(session, segment)], cancellationToken);
        await context.Collection<TrashEntry>("trash").Indexes.CreateOneAsync(new CreateIndexModel<TrashEntry>(
            Builders<TrashEntry>.IndexKeys.Ascending(x => x.EntityId),
            new CreateIndexOptions { Name = "trash_entity_unique", Unique = true }), cancellationToken: cancellationToken);
        await PersistAsync(journal, cancellationToken);
        await CompleteMoveAsync(journal, cancellationToken);
    }

    /// <summary>恢复当前分段回收条目，合并最新清单并保留原始时间偏移。</summary>
    public async Task RestoreAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var journal = await CurrentAsync(entry.Id, cancellationToken);
        if (journal is null)
        {
            return;
        }
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

    /// <summary>彻底移除当前分段回收条目，仅清理没有其他引用的媒体。</summary>
    public async Task PurgeAsync(TrashEntry entry, CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var journal = await CurrentAsync(entry.Id, cancellationToken);
        if (journal is null)
        {
            return;
        }
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

    /// <summary>恢复中断的分段操作，并依据当前回收站保留期限清理到期条目。</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        foreach (var journal in await SessionSegmentTrashFiles.ReadAllAsync(paths.RecordsDirectory, cancellationToken))
        {
            try
            {
                await ResumeAsync(journal, cancellationToken);
                if (!File.Exists(JournalPath(journal.Entry.Id)))
                {
                    continue;
                }
                journal.Entry.PurgeAtUtc = TrashRetentionPolicy.GetPurgeAtUtc(journal.Entry,
                    await settingsStore.GetAsync(cancellationToken));
                await PersistAsync(journal, cancellationToken);
                if (journal.Entry.PurgeAtUtc <= DateTime.UtcNow)
                {
                    await CompletePurgeAsync(journal, cancellationToken);
                }
            }
            catch (InfrastructureException error) when (error.StatusCode is 409 or 410)
            {
            }
        }
    }

    internal async Task ReconcileUnderLibraryLockAsync(CancellationToken cancellationToken)
    {
        foreach (var journal in await SessionSegmentTrashFiles.ReadAllAsync(paths.RecordsDirectory, cancellationToken))
        {
            await ResumeAsync(journal, cancellationToken);
        }
    }

    private Task ResumeAsync(SessionSegmentTrashJournal journal, CancellationToken cancellationToken)
    {
        return journal.Entry.State switch
        {
            "Restoring" or "Restored" => CompleteRestoreAsync(journal, cancellationToken),
            "Purging" => CompletePurgeAsync(journal, cancellationToken),
            _ => CompleteMoveAsync(journal, cancellationToken)
        };
    }

    private async Task CompleteMoveAsync(SessionSegmentTrashJournal journal, CancellationToken cancellationToken)
    {
        if (journal.Entry.State == "Active")
        {
            await context.Collection<RecordingSegment>("segments")
                .DeleteOneAsync(x => x.Id == journal.Segment.Id && x.SessionId == journal.Segment.SessionId,
                    cancellationToken);
            await PersistAsync(journal, cancellationToken);
            return;
        }
        var session = await RequireSessionAsync(journal.Segment.SessionId, cancellationToken);
        RequireRevision(session, journal);
        journal.Entry.State = "Moving";
        await PersistAsync(journal, cancellationToken);
        await SaveSegmentAsync(session, journal.Segment, false, cancellationToken);
        if (!journal.Entry.RetainedInPlace)
        {
            MoveToTrash(journal.MediaRelativePath, journal.Entry.TrashRelativePath);
            if (journal.IndexRelativePath is { } index)
            {
                MoveToTrash(index, TrashIndexPath(journal));
            }
        }
        journal.Entry.State = "Active";
        await PersistAsync(journal, cancellationToken);
        await new TranscodeLifecycle(context, paths).ReconcileUnderLibraryLockAsync(cancellationToken,
            [TranscodeAssetRegistry.AssetId(new() { DirectoryPath = journal.SessionDirectory }, journal.Segment)]);
    }

    private async Task CompleteRestoreAsync(SessionSegmentTrashJournal journal, CancellationToken cancellationToken)
    {
        await new TranscodeLifecycle(context, paths).MarkDirtyAsync(
            [TranscodeAssetRegistry.AssetId(new() { DirectoryPath = journal.SessionDirectory }, journal.Segment)], cancellationToken);
        if (journal.Entry.State == "Restored")
        {
            await FinishAsync(journal, cancellationToken);
            return;
        }
        var session = await RequireSessionAsync(journal.Segment.SessionId, cancellationToken);
        RequireRevision(session, journal);
        var resuming = journal.Entry.State == "Restoring";
        if (journal.Entry.RetainedInPlace)
        {
            RequireFile(journal.MediaRelativePath);
            if (journal.IndexRelativePath is { } index)
            {
                RequireFile(index);
            }
        }
        else
        {
            ValidateRestore(journal.Entry.TrashRelativePath, journal.MediaRelativePath, resuming);
            if (journal.IndexRelativePath is { } index)
            {
                ValidateRestore(TrashIndexPath(journal), index, resuming);
            }
        }
        journal.Entry.State = "Restoring";
        await PersistAsync(journal, cancellationToken);
        if (!journal.Entry.RetainedInPlace)
        {
            MoveFromTrash(journal.Entry.TrashRelativePath, journal.MediaRelativePath);
            if (journal.IndexRelativePath is { } index)
            {
                MoveFromTrash(TrashIndexPath(journal), index);
            }
        }
        await SaveSegmentAsync(session, journal.Segment, true, cancellationToken);
        journal.Entry.State = "Restored";
        await PersistAsync(journal, cancellationToken);
        await FinishAsync(journal, cancellationToken);
    }

    private async Task CompletePurgeAsync(SessionSegmentTrashJournal journal, CancellationToken cancellationToken)
    {
        await new TranscodeLifecycle(context, paths).MarkDirtyAsync(
            [TranscodeAssetRegistry.AssetId(new() { DirectoryPath = journal.SessionDirectory }, journal.Segment)], cancellationToken);
        journal.Entry.State = "Purging";
        await PersistAsync(journal, cancellationToken);
        if (journal.Entry.RetainedInPlace)
        {
            var references = await new MediaFileReferenceService(context, paths)
                .GetReferencedFilesAsync(journal.Segment.Id, journal.Entry.Id, cancellationToken);
            foreach (var relative in new[] { journal.MediaRelativePath, journal.IndexRelativePath }.OfType<string>())
            {
                var path = MediaPaths.Resolve(paths.RecordsDirectory, relative);
                if (!references.Contains(path))
                {
                    File.Delete(path);
                }
            }
        }
        else
        {
            File.Delete(MediaPaths.Resolve(paths.RecordsDirectory, journal.Entry.TrashRelativePath));
            if (journal.IndexRelativePath is not null)
            {
                File.Delete(MediaPaths.Resolve(paths.RecordsDirectory, TrashIndexPath(journal)));
            }
        }
        await context.Collection<RecordingSegment>("segments")
            .DeleteOneAsync(x => x.Id == journal.Segment.Id && x.SessionId == journal.Segment.SessionId,
                cancellationToken);
        await FinishAsync(journal, cancellationToken);
    }

    private async Task SaveSegmentAsync(BroadcastSession session, RecordingSegment segment, bool restoring,
        CancellationToken cancellationToken)
    {
        segment.AssetId = TranscodeAssetRegistry.AssetId(session, segment);
        var segments = await SegmentsAsync(session.Id, cancellationToken);
        var path = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(session.DirectoryPath, "场次.json"));
        if (File.Exists(path))
        {
            await using var input = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<SessionManifest>(input, JsonFiles.Options,
                               cancellationToken)
                           ?? throw new InvalidDataException("Invalid session manifest.");
            if (manifest.Session.Id != session.Id || manifest.Session.MediaRevision != session.MediaRevision)
            {
                throw new InfrastructureException(MessageCode.SEGMENT_TRASH_SESSION_CHANGED, 409);
            }
            foreach (var item in manifest.Segments)
            {
                if (segments.All(x => x.Id != item.Id))
                {
                    segments.Add(item);
                }
            }
        }
        segments.RemoveAll(x => x.Id == segment.Id);
        var removedIds = (await SessionSegmentTrashFiles.ReadAllAsync(paths.RecordsDirectory, cancellationToken))
            .Where(x => x.Entry.SessionId == session.Id && x.Segment.Id != segment.Id &&
                        x.Entry.State is not ("Restoring" or "Restored"))
            .Select(x => x.Segment.Id).ToHashSet(StringComparer.Ordinal);
        segments.RemoveAll(x => removedIds.Contains(x.Id));
        if (restoring)
        {
            segments.Add(segment);
        }
        await context.Collection<RecordingSegmentReceipt>("recordingSegments").UpdateOneAsync(value => value.Id == segment.Id && value.SessionId == session.Id,
            Builders<RecordingSegmentReceipt>.Update.Set(value => value.Retired, !restoring), cancellationToken: cancellationToken);
        session.TotalBytes = segments.Where(x => x.Revision == session.MediaRevision).Sum(x => x.CommittedBytes);
        await SessionManifestFiles.WriteAsync(path, new()
        {
            Session = session,
            Segments = segments.OrderBy(x => x.StartOffsetMs).ToList()
        }, cancellationToken);
        if (restoring)
        {
            await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id,
                segment, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
        else
        {
            await context.Collection<RecordingSegment>("segments")
                .DeleteOneAsync(x => x.Id == segment.Id && x.SessionId == session.Id, cancellationToken);
        }
        await context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.TotalBytes, session.TotalBytes),
            cancellationToken: cancellationToken);
    }

    private async Task<BroadcastSession> RequireSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await context.Collection<BroadcastSession>("sessions").Find(x => x.Id == sessionId)
                          .FirstOrDefaultAsync(cancellationToken)
                      ?? throw new InfrastructureException(MessageCode.LIBRARY_SESSION_NOT_FOUND, 404);
        if (session.IsTrashed)
        {
            throw new InfrastructureException(MessageCode.SEGMENT_TRASH_SESSION_TRASHED, 409);
        }
        if (session.IsHidden || !session.ProjectionReady)
        {
            throw new InfrastructureException(MessageCode.LIBRARY_DELETE_NOT_EDITABLE, 409);
        }
        await LiveMarkerProjectionGuard.EnsureWritableAsync(paths.RecordsDirectory, sessionId, cancellationToken);
        await LiveMarkerProjectionGuard.EnsureNoTrashIntentAsync(paths.RecordsDirectory, sessionId, cancellationToken);
        return session;
    }

    private static void RequireRevision(BroadcastSession session, SessionSegmentTrashJournal journal)
    {
        if (session.MediaRevision != journal.MediaRevision || session.DirectoryPath != journal.SessionDirectory)
        {
            throw new InfrastructureException(MessageCode.SEGMENT_TRASH_SESSION_CHANGED, 409);
        }
    }

    private Task<List<RecordingSegment>> SegmentsAsync(string sessionId, CancellationToken cancellationToken)
    {
        return context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == sessionId)
            .ToListAsync(cancellationToken);
    }

    private void MoveToTrash(string sourceRelative, string destinationRelative)
    {
        var source = MediaPaths.Resolve(paths.RecordsDirectory, sourceRelative);
        var destination = MediaPaths.Resolve(paths.RecordsDirectory, destinationRelative);
        if (File.Exists(source) && File.Exists(destination))
        {
            throw new InfrastructureException(MessageCode.STORAGE_TRASH_PATH_CONFLICT, 409);
        }
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
    }

    private void ValidateRestore(string sourceRelative, string destinationRelative, bool resuming)
    {
        var source = MediaPaths.Resolve(paths.RecordsDirectory, sourceRelative);
        var destination = MediaPaths.Resolve(paths.RecordsDirectory, destinationRelative);
        if (File.Exists(destination) && (!resuming || File.Exists(source)))
        {
            throw new InfrastructureException(MessageCode.STORAGE_RESTORE_DESTINATION_EXISTS, 409);
        }
        if (!File.Exists(source) && !File.Exists(destination))
        {
            throw new InfrastructureException(MessageCode.STORAGE_TRASH_FILE_MISSING, 410);
        }
    }

    private void MoveFromTrash(string sourceRelative, string destinationRelative)
    {
        var source = MediaPaths.Resolve(paths.RecordsDirectory, sourceRelative);
        if (File.Exists(source))
        {
            var destination = MediaPaths.Resolve(paths.RecordsDirectory, destinationRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
    }

    private void RequireFile(string relative)
    {
        if (!File.Exists(MediaPaths.Resolve(paths.RecordsDirectory, relative)))
        {
            throw new InfrastructureException(MessageCode.STORAGE_TRASH_FILE_MISSING, 410);
        }
    }

    private static string TrashIndexPath(SessionSegmentTrashJournal journal)
    {
        return Path.Combine(".trash", journal.Entry.Id, "segment", "index" + Path.GetExtension(journal.IndexRelativePath));
    }

    private string JournalPath(string entryId)
    {
        return SessionSegmentTrashFiles.JournalPath(paths.RecordsDirectory, entryId);
    }

    private async Task<SessionSegmentTrashJournal?> CurrentAsync(string entryId, CancellationToken cancellationToken)
    {
        var path = JournalPath(entryId);
        return File.Exists(path) ? await SessionSegmentTrashFiles.ReadAsync(path, cancellationToken) : null;
    }

    private async Task PersistAsync(SessionSegmentTrashJournal journal, CancellationToken cancellationToken)
    {
        await JsonFiles.WriteAsync(JournalPath(journal.Entry.Id), journal, cancellationToken);
        await context.Collection<TrashEntry>("trash").ReplaceOneAsync(x => x.Id == journal.Entry.Id,
            journal.Entry, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    private async Task FinishAsync(SessionSegmentTrashJournal journal, CancellationToken cancellationToken)
    {
        await context.Collection<TrashEntry>("trash")
            .DeleteOneAsync(x => x.Id == journal.Entry.Id && x.Kind == "Segment", cancellationToken);
        var directory = Path.GetDirectoryName(JournalPath(journal.Entry.Id))!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
        await new TranscodeLifecycle(context, paths).ReconcileUnderLibraryLockAsync(cancellationToken,
            [TranscodeAssetRegistry.AssetId(new() { DirectoryPath = journal.SessionDirectory }, journal.Segment)]);
    }
}
