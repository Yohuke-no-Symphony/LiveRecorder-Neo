using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class BandwidthTestSettingsStore : ISettingsStore
{
    internal ServerSettings Settings { get; set; } = new();
    internal bool Fail { get; set; }

    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) =>
        Fail ? Task.FromException<ServerSettings>(new IOException("Settings unavailable")) : Task.FromResult(Settings);

    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default)
    {
        Settings = update(Settings);
        return Task.FromResult(Settings);
    }
}
