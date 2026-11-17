using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class TaskLogRetentionFailureSettingsStore(ISettingsStore inner, Action beforeWrite) : ISettingsStore
{
    internal int Updates { get; private set; }

    /// <inheritdoc />
    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default) => inner.GetAsync(cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default) => inner.SaveAsync(settings, cancellationToken);

    /// <inheritdoc />
    public Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default) =>
        inner.UpdateAsync(previous =>
        {
            var next = update(previous);
            Updates++;
            beforeWrite();
            return next;
        }, cancellationToken);
}
