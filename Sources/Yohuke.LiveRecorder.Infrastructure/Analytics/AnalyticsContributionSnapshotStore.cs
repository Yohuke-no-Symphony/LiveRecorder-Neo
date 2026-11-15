using System.Text.Json;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal sealed class AnalyticsContributionSnapshotStore(IAppPaths paths)
{
    private const long DISK_LIMIT_BYTES = 512 * 1024 * 1024;
    private static JsonSerializerOptions SnapshotJsonOptions { get; } = new(JsonFiles.Options) { RespectNullableAnnotations = true };
    private static TimeSpan Retention { get; } = TimeSpan.FromMinutes(10);
    private string DirectoryPath => Path.Combine(paths.CacheDirectory, "analytics-contributions");

    internal async Task<AnalyticsContributionSnapshot?> ReadAsync(string identity, CancellationToken cancellationToken)
    {
        var path = Path.Combine(DirectoryPath, identity + ".json");
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - Retention)
            {
                return null;
            }
            AnalyticsContributionSnapshot? result;
            await using (var stream = File.OpenRead(path))
            {
                result = await JsonSerializer.DeserializeAsync<AnalyticsContributionSnapshot>(stream, SnapshotJsonOptions, cancellationToken);
            }
            if (result is not { SchemaVersion: AnalyticsContributionSnapshot.CURRENT_SCHEMA_VERSION, Events: not null, Users: not null, Summary: not null } ||
                string.IsNullOrWhiteSpace(result.SessionId) || result.Kind is not ("SuperChat" or "Gift" or "Guard" or "RedPocket" or "All") ||
                !double.IsFinite(result.FromMs) || !double.IsFinite(result.ToMs) || result.FromMs < 0 || result.ToMs < result.FromMs ||
                result.Events.Any(item => item is null) || result.Users.Any(item => item is null))
            {
                return null;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal async Task WriteAsync(string identity, AnalyticsContributionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = Path.Combine(DirectoryPath, identity + ".json");
        await JsonFiles.WriteAsync(path, snapshot, cancellationToken);
        var files = new DirectoryInfo(DirectoryPath).GetFiles("*.json").OrderBy(file => file.LastWriteTimeUtc).ToList();
        var total = files.Sum(file => file.Length);
        var expiration = DateTime.UtcNow - Retention;
        foreach (var file in files)
        {
            if (file.FullName == path || file.LastWriteTimeUtc >= expiration && total <= DISK_LIMIT_BYTES)
            {
                continue;
            }
            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (IOException)
            {
            }
        }
    }
}
