using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class PausingSettingsStore(ISettingsStore inner) : ISettingsStore
{
    internal TaskCompletionSource Updated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => inner.GetAsync(cancellationToken);
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default) => inner.SaveAsync(settings, cancellationToken);

    public async Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default)
    {
        var settings = await inner.UpdateAsync(update, cancellationToken);
        Updated.TrySetResult();
        await Resume.Task.WaitAsync(cancellationToken);
        return settings;
    }
}
