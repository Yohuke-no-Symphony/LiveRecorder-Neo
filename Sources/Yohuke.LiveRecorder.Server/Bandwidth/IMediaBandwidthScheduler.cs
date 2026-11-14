namespace Yohuke.LiveRecorder.Server.Bandwidth;

/// <summary>按账号和媒体类别分配共享出口的发送许可。</summary>
public interface IMediaBandwidthScheduler
{
    /// <summary>注册一次媒体传输，在返回前读取有效的带宽配置。</summary>
    ValueTask<MediaBandwidthTransfer> OpenTransferAsync(string userId, MediaTrafficKind kind, CancellationToken cancellationToken = default);
}
