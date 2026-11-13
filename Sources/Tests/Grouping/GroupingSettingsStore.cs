using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Grouping.Tests;

internal sealed class GroupingSettingsStore : ISettingsStore
{
    internal ServerSettings Value { get; } = new() { MinimumFreeBytes = 0 };
    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
