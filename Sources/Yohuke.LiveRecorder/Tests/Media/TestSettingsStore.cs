using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Media.Tests;

internal sealed class TestSettingsStore : ISettingsStore
{
    internal ServerSettings Value { get; } = new();

    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Value);
    }

    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
