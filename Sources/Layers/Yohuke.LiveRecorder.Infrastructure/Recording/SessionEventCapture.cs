using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>一场直播的弹幕任务、队列与取消令牌的统一生命周期。</summary>
public sealed class SessionEventCapture : IAsyncDisposable
{
    private readonly SessionEventWriter? writer;
    private readonly CancellationTokenSource cancellation;
    private readonly Task capture;

    /// <summary>创建归档队列并启动接收，调用方必须异步释放。</summary>
    public SessionEventCapture(BroadcastSession session, Streamer target, string directory, Func<BroadcastSession, Streamer, SessionEventWriter?, CancellationToken, Task> receive, CancellationToken cancellationToken, bool archive = true)
    {
        writer = archive ? new SessionEventWriter(Path.Combine(directory, "弹幕")) : null;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        capture = receive(session, target, writer, cancellation.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        try
        {
            try
            {
                await capture;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            try
            {
                if (writer is not null)
                {
                    await writer.DisposeAsync();
                }
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }
}
