using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal static class UserAvatarFiles
{
    internal const string ROUTE_PREFIX = "/avatars/";

    internal static string LegacyUrl(string id) => $"/api/auth{ROUTE_PREFIX}{Uri.EscapeDataString(id)}";

    internal static string VersionedUrl(string id, string revision) => $"{LegacyUrl(id)}?v={revision}";

    internal static Task WriteAsync(IAppPaths paths, string id, string revision, byte[] content, CancellationToken ct)
    {
        var path = Resolve(paths, id, revision);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return PrivateFiles.WriteAllBytesAsync(path, content, ct);
    }

    internal static async Task<UserAvatarImage?> ReadAsync(IAppPaths paths, string id, string revision, CancellationToken ct)
    {
        if (!IsRevision(revision))
        {
            return null;
        }
        try
        {
            await using var stream = new FileStream(Resolve(paths, id, revision), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, useAsync: true);
            var length = stream.Length;
            if (length is < 12 or > UserAvatarImage.MAX_IMAGE_BYTES)
            {
                return null;
            }
            var content = new byte[(int)length];
            await stream.ReadExactlyAsync(content, ct);
            return UserAvatarImage.FromBytes(content);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException)
        {
            return null;
        }
    }

    internal static void TryDelete(IAppPaths paths, string id, string? revision, ILogger logger)
    {
        if (!IsRevision(revision))
        {
            return;
        }
        try
        {
            File.Delete(Resolve(paths, id, revision!));
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("User avatar cleanup failed: {ErrorType}", error.GetType().Name);
        }
    }

    private static bool IsRevision(string? revision) =>
        revision is { Length: 32 or 64 } && revision.All(char.IsAsciiHexDigit);

    private static string Resolve(IAppPaths paths, string id, string revision)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!IsRevision(revision))
        {
            throw new ArgumentException("Invalid avatar revision.", nameof(revision));
        }
        var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return Path.Combine(paths.DataDirectory, "avatars", account, revision + ".img");
    }
}
