using Microsoft.Extensions.Logging;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>在同一次连接中创建平台 Socket、弹幕补录和红包恢复服务。</summary>
public sealed class BilibiliEventConnection(IHttpClientFactory httpClientFactory, ILogger<BilibiliEventConnection> logger) : IBilibiliEventConnection
{
    /// <inheritdoc />
    public async Task RunAsync(long roomId, string cookie, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        BilibiliDanmuReconciliation reconciliation, CancellationToken cancellationToken, CancellationToken credentialsChanged)
    {
        using var socket = new BilibiliLiveWebSocket(roomId, cookie) { ReconnectMode = LiveDanmuReconnectMode.CallerManaged };
        using var recoveryHttp = httpClientFactory.CreateClient("bilibili");
        using var subscription = new BilibiliEventSubscription(socket, socket, roomId, cookie,
            new BilibiliDanmuHistoryService(recoveryHttp), new BilibiliRedPocketService(recoveryHttp), logger,
            cancellationToken, credentialsChanged, reconciliation: reconciliation);
        await subscription.RunAsync(onEvent, cancellationToken);
    }
}
