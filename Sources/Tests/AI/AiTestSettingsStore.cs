using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiTestSettingsStore(ServerSettings value) : ISettingsStore
{
    internal ServerSettings Value { get; private set; } = value;

    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);

    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        Value = settings;
        return Task.CompletedTask;
    }
    public Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default)
    {
        Value = update(Value);
        return Task.FromResult(Value);
    }

}
