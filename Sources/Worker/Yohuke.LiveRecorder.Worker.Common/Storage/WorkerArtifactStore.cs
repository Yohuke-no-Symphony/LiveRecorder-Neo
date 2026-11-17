using System.Security.Cryptography;

namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal static class WorkerArtifactStore
{
    internal static async Task<List<WorkerArtifact>> CaptureAsync(string root, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var result = new List<WorkerArtifact>();
        foreach (var relative in paths.Order(StringComparer.Ordinal))
        {
            var path = Resolve(root, relative);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            result.Add(new()
            {
                RelativePath = relative, Length = stream.Length,
                Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken))
            });
        }
        return result;
    }

    internal static async Task<bool> ValidateAsync(string root, IReadOnlyList<WorkerArtifact> artifacts, CancellationToken cancellationToken)
    {
        if (artifacts.Count == 0)
        {
            return false;
        }
        try
        {
            foreach (var artifact in artifacts)
            {
                var path = Resolve(root, artifact.RelativePath);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != artifact.Length || !string.Equals(artifact.Sha256,
                        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string Resolve(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative))
        {
            throw new InvalidDataException("Checkpoint artifacts must use relative paths.");
        }
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("A checkpoint artifact escaped its workspace.");
        }
        return path;
    }
}
