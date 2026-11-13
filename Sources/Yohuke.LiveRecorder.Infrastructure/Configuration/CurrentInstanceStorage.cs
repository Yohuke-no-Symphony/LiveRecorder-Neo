namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>以启动或完成安装时发布的快照确定就绪状态，后续文件写入不重新开放安装入口。</summary>
public sealed class CurrentInstanceStorage(InstanceStorageContext initial)
{
    private InstanceStorageContext current = initial;

    public InstanceStorageContext Current => Volatile.Read(ref current);
    public bool IsInstalled => Current.IsInstalled;

    /// <summary>发布已完成安装的候选；拒绝替换已安装实例或覆盖其他安装流程的结果。</summary>
    public void PublishInstalled(InstanceStorageContext expected, InstanceStorageContext candidate)
    {
        if (expected.IsInstalled || !ReferenceEquals(Current, expected) || !candidate.Bootstrap.IsInstalled)
        {
            throw new InvalidOperationException("仅能为当前未安装实例发布已经完成验证的安装候选。");
        }
        var installed = new InstanceStorageContext(candidate.Paths, candidate.DataProtectionProvider, candidate.SecretProtector, candidate.Bootstrap, candidate.Mongo, true);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref current, installed, expected), expected))
        {
            throw new InvalidOperationException("实例已由另一个安装流程更新。");
        }
    }
}
