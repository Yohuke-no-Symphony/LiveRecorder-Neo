using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class JobTestSettingsStore(string root) : ISettingsStore
{
    internal ServerSettings Settings { get; } = new() { StoragePath = root, MinimumFreeBytes = 0, MediaConcurrency = 1 };

    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
