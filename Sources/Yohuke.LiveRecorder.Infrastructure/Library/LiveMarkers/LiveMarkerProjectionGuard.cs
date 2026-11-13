using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

internal static class LiveMarkerProjectionGuard
{
    internal static async Task EnsureNoTrashIntentAsync(string recordsRoot, string sessionId,
        CancellationToken cancellationToken)
    {
        var root = MediaPaths.Resolve(recordsRoot, ".trash");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(directory, "session-entry.json");
            if (!File.Exists(path))
            {
                continue;
            }

            await using var input = File.OpenRead(path);
            var journal =
                await JsonSerializer.DeserializeAsync<SessionTrashJournal>(input, JsonFiles.Options, cancellationToken);
            if (journal?.Entry.EntityId == sessionId)
            {
                throw new InfrastructureException(MessageCode.LIVE_MARKER_SESSION_NOT_AVAILABLE, 409);
            }
        }
    }

    internal static async Task EnsureWritableAsync(string recordsRoot, string sessionId,
        CancellationToken cancellationToken)
    {
        var root = MediaPaths.Resolve(recordsRoot, Path.Combine(".work", "groups"));
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "operation.json", SearchOption.AllDirectories))
        {
            await using var input = File.OpenRead(path);
            var operation =
                await JsonSerializer.DeserializeAsync<ProjectionOperation>(input, JsonFiles.Options, cancellationToken);
            if (operation is not null && (operation.SourceIds.Contains(sessionId) ||
                                          operation.Outputs.Any(output => output.Session.Id == sessionId)))
            {
                throw new InfrastructureException(MessageCode.LIVE_MARKER_SESSION_NOT_AVAILABLE, 409);
            }
        }
    }
}
