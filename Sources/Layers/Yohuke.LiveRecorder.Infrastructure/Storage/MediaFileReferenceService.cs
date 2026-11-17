using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

internal sealed class MediaFileReferenceService(IMongoContext context, IAppPaths paths)
{
    internal async Task<HashSet<string>> GetReferencedFilesAsync(string excludedSegmentId, string? excludedEntryId,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var sessions = await context.Collection<BroadcastSession>("sessions")
            .Find(FilterDefinition<BroadcastSession>.Empty).ToListAsync(cancellationToken);
        var segments = await context.Collection<RecordingSegment>("segments")
            .Find(x => x.Id != excludedSegmentId).ToListAsync(cancellationToken);
        foreach (var path in Directory.EnumerateFiles(paths.RecordsDirectory, "场次.json",
                     new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (!SessionTrashService.CanReconcile(paths.RecordsDirectory, path))
            {
                continue;
            }
            var manifest = await ReadAsync<SessionManifest>(path, cancellationToken);
            sessions.Add(manifest.Session);
            foreach (var segment in manifest.Segments.Where(x => x.Id != excludedSegmentId))
            {
                AddSegment(result, manifest.Session.DirectoryPath, segment);
            }
        }

        var trashRoot = MediaPaths.Resolve(paths.RecordsDirectory, ".trash");
        if (Directory.Exists(trashRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(trashRoot))
            {
                var path = MediaPaths.Resolve(paths.RecordsDirectory,
                    Path.Combine(".trash", Path.GetFileName(directory), "session-entry.json"));
                if (!File.Exists(path))
                {
                    continue;
                }
                var journal = await ReadAsync<SessionTrashJournal>(path, cancellationToken);
                sessions.Add(journal.Manifest.Session);
                foreach (var segment in journal.Manifest.Segments.Where(x => x.Id != excludedSegmentId))
                {
                    AddSegment(result, journal.Manifest.Session.DirectoryPath, segment);
                }
            }
        }

        foreach (var session in sessions)
        {
            AddPath(result, session.CoverRelativePath);
        }
        foreach (var segment in segments)
        {
            AddSegment(result, sessions.FirstOrDefault(x => x.Id == segment.SessionId)?.DirectoryPath, segment);
        }
        foreach (var journal in await SessionSegmentTrashFiles.ReadAllAsync(paths.RecordsDirectory, cancellationToken))
        {
            if (journal.Entry.Id != excludedEntryId && journal.Entry.RetainedInPlace)
            {
                AddPath(result, journal.MediaRelativePath);
                AddPath(result, journal.IndexRelativePath);
            }
        }

        var jobs = await context.Collection<MediaJob>("jobs")
            .Find(x => x.State == "Queued" || x.State == "Running" || x.State == "Publishing" || x.State == "Completed")
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            if (job.State == "Completed")
            {
                if (job.Kind != "Clip")
                {
                    foreach (var output in job.OutputRelativePaths)
                    {
                        AddPath(result, output);
                    }
                }
                continue;
            }
            foreach (var segment in job.SourceSegments)
            {
                AddSegment(result, sessions.FirstOrDefault(x => x.Id == job.SessionId)?.DirectoryPath, segment);
            }
        }
        foreach (var clip in await context.Collection<Clip>("clips").Find(x => !x.IsTrashed || x.PublicationPending)
                     .ToListAsync(cancellationToken))
        {
            AddPath(result, clip.RelativePath);
        }
        foreach (var entry in await context.Collection<TrashEntry>("trash").Find(x => x.Kind == "Clip")
                     .ToListAsync(cancellationToken))
        {
            AddPath(result, entry.OriginalRelativePath);
            AddPath(result, entry.TrashRelativePath);
        }
        return result;
    }

    private void AddSegment(HashSet<string> result, string? directory, RecordingSegment segment)
    {
        AddPath(result, segment.RootRelativePath ?? (directory is null ? null : Path.Combine(directory, segment.RelativePath)));
        AddPath(result, segment.RootIndexRelativePath ?? (directory is null || string.IsNullOrEmpty(segment.IndexRelativePath)
            ? null
            : Path.Combine(directory, segment.IndexRelativePath)));
    }

    private void AddPath(HashSet<string> result, string? relative)
    {
        if (!string.IsNullOrEmpty(relative))
        {
            result.Add(MediaPaths.Resolve(paths.RecordsDirectory, relative));
        }
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonFiles.Options, cancellationToken)
               ?? throw new InvalidDataException("Invalid media reference manifest.");
    }
}
