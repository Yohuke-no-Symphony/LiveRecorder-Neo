using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

internal static class SessionManifestFiles
{
    internal static Task WriteAsync(string path, SessionManifest manifest,
        CancellationToken cancellationToken = default)
    {
        return WriteAsync(path, manifest, true, cancellationToken);
    }

    internal static async Task WriteAsync(string path, SessionManifest manifest, bool preserveRetention,
        CancellationToken cancellationToken)
    {
        await using var fileLock = await AcquireAsync(path, cancellationToken);
        if (preserveRetention && File.Exists(path))
        {
            var current = await ReadCoreAsync(path, cancellationToken);
            manifest.Session.IsPermanent = current.Session.IsPermanent;
            manifest.Session.RetentionStartedAtUtc = current.Session.RetentionStartedAtUtc;
        }

        await JsonFiles.WriteAsync(path, manifest, cancellationToken);
    }

    internal static async Task SetPermanentAsync(SessionManifest fallback, string directory, bool isPermanent,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "场次.json");
        await using var fileLock = await AcquireAsync(path, cancellationToken);
        var manifest = File.Exists(path) ? await ReadCoreAsync(path, cancellationToken) : fallback;
        manifest.Session.IsPermanent = isPermanent;
        await JsonFiles.WriteAsync(path, manifest, cancellationToken);
    }

    internal static async Task<BroadcastSession?> ReadSessionAsync(string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var fileLock = await AcquireAsync(path, cancellationToken);
        return File.Exists(path) ? (await ReadCoreAsync(path, cancellationToken)).Session : null;
    }

    private static async Task<FileStream> AcquireAsync(string manifestPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var lockPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, ".session-manifest.lock");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (IOException)
            {
                throw new InfrastructureException(MessageCode.STORAGE_OPERATION_IN_PROGRESS, 409);
            }
        }
    }

    private static async Task<SessionManifest> ReadCoreAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionManifest>(input, JsonFiles.Options, cancellationToken)
               ?? throw new InvalidDataException("Invalid session manifest.");
    }
}
