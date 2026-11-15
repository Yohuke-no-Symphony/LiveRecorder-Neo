using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

internal static class SessionSegmentTrashFiles
{
    internal const string JOURNAL_NAME = "segment-entry.json";

    internal static string JournalPath(string recordsRoot, string entryId)
    {
        return MediaPaths.Resolve(recordsRoot, Path.Combine(".trash", entryId, JOURNAL_NAME));
    }

    internal static async Task<List<SessionSegmentTrashJournal>> ReadAllAsync(string recordsRoot,
        CancellationToken cancellationToken)
    {
        var root = MediaPaths.Resolve(recordsRoot, ".trash");
        var journals = new List<SessionSegmentTrashJournal>();
        if (!Directory.Exists(root))
        {
            return journals;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var path = JournalPath(recordsRoot, Path.GetFileName(directory));
            if (File.Exists(path))
            {
                journals.Add(await ReadAsync(path, cancellationToken));
            }
        }
        return journals;
    }

    internal static async Task<SessionSegmentTrashJournal> ReadAsync(string path,
        CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionSegmentTrashJournal>(input, JsonFiles.Options,
                   cancellationToken)
               ?? throw new InvalidDataException("Invalid segment trash journal.");
    }

    internal static async Task EnsureNoEntriesAsync(string recordsRoot, string sessionId,
        CancellationToken cancellationToken)
    {
        if ((await ReadAllAsync(recordsRoot, cancellationToken)).Any(x => x.Entry.SessionId == sessionId))
        {
            throw new InfrastructureException(MessageCode.SEGMENT_TRASH_UNRESOLVED, 409);
        }
    }

    internal static async Task EnsureNoPendingAsync(string recordsRoot, string sessionId,
        CancellationToken cancellationToken)
    {
        if ((await ReadAllAsync(recordsRoot, cancellationToken))
            .Any(x => x.Entry.SessionId == sessionId && x.Entry.State != "Active"))
        {
            throw new InfrastructureException(MessageCode.STORAGE_OPERATION_IN_PROGRESS, 409);
        }
    }

    internal static async Task<List<string>> RetainedPathsAsync(string recordsRoot,
        CancellationToken cancellationToken)
    {
        return (await ReadAllAsync(recordsRoot, cancellationToken))
            .Where(x => x.Entry.RetainedInPlace)
            .SelectMany(x => new[] { x.MediaRelativePath, x.IndexRelativePath })
            .OfType<string>().ToList();
    }
}
