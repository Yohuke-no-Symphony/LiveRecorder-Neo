using Yohuke.LiveRecorder.Server.Bandwidth;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class FailingMediaBandwidthScheduler : IMediaBandwidthScheduler
{
    internal int Calls { get; private set; }

    /// <inheritdoc />
    public ValueTask<MediaBandwidthTransfer> OpenTransferAsync(string userId, MediaTrafficKind kind, CancellationToken cancellationToken = default)
    {
        Calls++;
        return ValueTask.FromException<MediaBandwidthTransfer>(new IOException("Bandwidth settings are unavailable."));
    }
}
