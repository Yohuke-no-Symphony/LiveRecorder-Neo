using System.Threading.Channels;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingReconciliationTestConnection : IBilibiliEventConnection
{
    private readonly Channel<int> attempts = Channel.CreateUnbounded<int>();
    private int connections;

    internal async Task NextAsync() => await attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    public async Task RunAsync(long roomId, string cookie, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        BilibiliDanmuReconciliation reconciliation, CancellationToken cancellationToken, CancellationToken credentialsChanged)
    {
        var attempt = Interlocked.Increment(ref connections);
        var message = new BilibiliDanmuMessage
        {
            RoomId = roomId.ToString(), Uid = 42, UserName = "Viewer", Content = "Retained message",
            MessageId = "stable-message", Timestamp = 1800000000, TimestampMilliseconds = 1800000000100
        };
        var accepted = attempt == 1 ? reconciliation.AcceptLive(message) : reconciliation.AcceptHistory([message]).Count > 0;
        if (accepted)
        {
            await onEvent(new() { Kind = "Danmu", Text = message.Content }, cancellationToken);
        }
        await attempts.Writer.WriteAsync(attempt, cancellationToken);
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
