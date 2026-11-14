namespace Yohuke.LiveRecorder.Server.Bandwidth;

/// <summary>一次有界读取的字节许可，归还未实际使用的额度。</summary>
public sealed class MediaBandwidthPermit : IDisposable
{
    private readonly Action<int> finish;
    private int completed;

    internal MediaBandwidthPermit(int bytes, Action<int> finish)
    {
        Bytes = bytes;
        this.finish = finish;
    }

    public int Bytes { get; }

    /// <summary>提交实际读取字节数并归还剩余许可。</summary>
    public void Commit(int bytesUsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesUsed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytesUsed, Bytes);
        if (Interlocked.Exchange(ref completed, 1) == 0)
        {
            finish(Bytes - bytesUsed);
        }
    }

    /// <summary>未提交的许可全部归还；重复释放不重复退款。</summary>
    public void Dispose() => Commit(0);
}
