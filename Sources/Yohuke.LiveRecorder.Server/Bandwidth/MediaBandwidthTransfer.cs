namespace Yohuke.LiveRecorder.Server.Bandwidth;

/// <summary>一次媒体响应的调度注册，同账号的多个注册共用账号预算。</summary>
public sealed class MediaBandwidthTransfer : IDisposable, IAsyncDisposable
{
    private readonly MediaBandwidthScheduler scheduler;

    internal MediaBandwidthTransfer(MediaBandwidthScheduler scheduler, BandwidthAccount account, MediaTrafficKind kind)
    {
        this.scheduler = scheduler;
        Account = account;
        Kind = kind;
    }

    internal BandwidthAccount Account { get; }
    internal MediaTrafficKind Kind { get; }
    internal BandwidthRequest? Pending { get; set; }
    internal bool HasPermit { get; set; }
    internal bool Closed { get; set; }

    /// <summary>申请不超过指定字节数的许可；完成后应提交或释放许可。</summary>
    public ValueTask<MediaBandwidthPermit> AcquireAsync(int maximumBytes, CancellationToken cancellationToken = default) =>
        scheduler.AcquireAsync(this, maximumBytes, cancellationToken);

    /// <summary>注销传输并取消尚未取得许可的读取。</summary>
    public void Dispose() => scheduler.Close(this);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
