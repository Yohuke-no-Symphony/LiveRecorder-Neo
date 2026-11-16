using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class CacheCleanupTestContext : IDisposable
{
    private readonly AudienceArtworkHttpHandler http = new();
    private readonly EventArchiveReader reader = new();
    internal StorageUsageTestContext Usage { get; } = new();
    internal AppPaths Paths => Usage.Paths;
    internal JobTestSettingsStore Settings { get; } = new();
    internal PlaybackCache Playback { get; }
    internal ArtworkCache Artwork { get; }
    internal AnalyticsContributionService Contributions { get; }
    internal CacheCleanupService Service { get; }

    internal CacheCleanupTestContext()
    {
        Playback = new(Paths, Settings);
        Artwork = new(http, Paths);
        Contributions = new(reader, Paths);
        Service = new(Playback, Artwork, Contributions, Usage.Service);
    }

    internal string CachePath(string relative) => Path.Combine(Paths.CacheDirectory, relative);

    internal Task WriteAsync(string relative, int bytes) => Usage.WriteAsync(Paths.CacheDirectory, relative, bytes);

    /// <inheritdoc />
    public void Dispose()
    {
        Service.Dispose();
        Contributions.Dispose();
        Artwork.Dispose();
        Playback.Dispose();
        reader.Dispose();
        http.Dispose();
        Usage.Dispose();
    }
}
