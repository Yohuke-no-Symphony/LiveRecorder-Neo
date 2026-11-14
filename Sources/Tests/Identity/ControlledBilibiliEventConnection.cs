using System.Threading.Channels;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class ControlledBilibiliEventConnection : IBilibiliEventConnection
{
    private readonly Channel<BilibiliConnectionObservation> connections = Channel.CreateUnbounded<BilibiliConnectionObservation>();

    internal async Task<BilibiliConnectionObservation> NextAsync() =>
        await connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public async Task RunAsync(long roomId, string cookie, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        BilibiliDanmuReconciliation reconciliation, CancellationToken cancellationToken, CancellationToken credentialsChanged)
    {
        await connections.Writer.WriteAsync(new(roomId, cookie, reconciliation, credentialsChanged), cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, credentialsChanged);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }
}
