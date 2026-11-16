using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>以指定凭据执行一条弹幕连接，凭据切换时排空已接收消息。</summary>
public interface IBilibiliEventConnection
{
    /// <summary>在录制取消或凭据生命周期结束时停止连接并完成归档。</summary>
    Task RunAsync(long roomId, string cookie, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent,
        BilibiliDanmuReconciliation reconciliation, CancellationToken cancellationToken, CancellationToken credentialsChanged);
}
