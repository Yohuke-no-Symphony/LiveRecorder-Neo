using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class JobTestSettingsStore : ISettingsStore
{
    internal ServerSettings Settings { get; } = new() { MinimumFreeBytes = 0, MediaConcurrency = 1 };

    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
