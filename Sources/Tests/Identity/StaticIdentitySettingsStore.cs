using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class StaticIdentitySettingsStore(ServerSettings settings) : ISettingsStore
{
    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
    public Task SaveAsync(ServerSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default)
    {
        settings = update(settings);
        return Task.FromResult(settings);
    }

}
