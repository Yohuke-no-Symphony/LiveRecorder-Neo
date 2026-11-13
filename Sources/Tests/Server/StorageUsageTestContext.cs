using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class StorageUsageTestContext : IDisposable
{
    private string Root { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-usage-tests", Guid.NewGuid().ToString("N"));
    internal AppPaths Paths { get; }
    internal ArtworkTestTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    internal StorageUsageService Service { get; }

    internal StorageUsageTestContext(bool nested = true)
    {
        Paths = new(Path.Combine(Root, "data"), nested ? Path.Combine(Root, "data", "records") : Path.Combine(Root, "records"));
        Paths.EnsureDirectories();
        Service = new(Paths, Clock);
    }

    internal async Task WriteAsync(string root, string relative, int bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[bytes]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Directory.Delete(Root, true);
    }
}
