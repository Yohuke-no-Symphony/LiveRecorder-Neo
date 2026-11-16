using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class RecordingRuntimeTestSettings : ISettingsStore
{
    private ServerSettings value = new() { MinimumFreeBytes = 0 };
    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        value = settings;
        return Task.CompletedTask;
    }
    public Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default)
    {
        value = update(value);
        return Task.FromResult(value);
    }
}
