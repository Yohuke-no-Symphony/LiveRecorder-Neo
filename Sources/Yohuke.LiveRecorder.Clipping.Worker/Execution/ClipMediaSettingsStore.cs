using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Clipping.Worker.Execution;

internal sealed class ClipMediaSettingsStore(WorkerConfiguration configuration) : ISettingsStore
{
    /// <inheritdoc />
    public Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ServerSettings
        {
            FfmpegPath = configuration.FfmpegPath,
            FfprobePath = configuration.FfprobePath,
            MediaConcurrency = configuration.MaxConcurrentEncodes,
            MinimumFreeBytes = configuration.MinimumFreeBytes
        });
    }

    /// <inheritdoc />
    public Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Clipping media tools use the job's immutable worker configuration.");

    /// <inheritdoc />
    public Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Clipping media tools use the job's immutable worker configuration.");
}
