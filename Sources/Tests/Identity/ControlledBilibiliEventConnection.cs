using System.Collections.Concurrent;
using System.Threading.Channels;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class ControlledBilibiliEventConnection : IBilibiliEventConnection
{
    private readonly Channel<BilibiliConnectionObservation> connections = Channel.CreateUnbounded<BilibiliConnectionObservation>();
    internal ConcurrentQueue<BilibiliConnectionObservation> Attempts { get; } = new();

    internal async Task<BilibiliConnectionObservation> NextAsync() =>
        await connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public async Task RunAsync(long roomId, string cookie, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        BilibiliDanmuReconciliation reconciliation, CancellationToken cancellationToken, CancellationToken credentialsChanged)
    {
        var observation = new BilibiliConnectionObservation(roomId, cookie, reconciliation, credentialsChanged);
        Attempts.Enqueue(observation);
        await connections.Writer.WriteAsync(observation, cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, credentialsChanged);
        try
        {
            if (await observation.Completion.Task.WaitAsync(lifetime.Token) is { } exception)
            {
                throw exception;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            observation.Stopped.TrySetResult();
        }
    }
}
