using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Capture;

internal sealed class RecordingMediaSettingsStore(WorkerConfiguration configuration) : ISettingsStore
{
    /// <inheritdoc />
    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ServerSettings { MinimumFreeBytes = configuration.MinimumFreeBytes });
    }

    /// <inheritdoc />
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Recording tools use the selected Worker configuration.");

    /// <inheritdoc />
    public Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Recording tools use the selected Worker configuration.");
}
