namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>以启动或完成安装时发布的快照确定就绪状态，后续文件写入不重新开放安装入口。</summary>
public sealed class CurrentInstanceStorage(InstanceStorageContext initial)
{
    private InstanceStorageContext current = initial;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InstanceStorageContext Current => Volatile.Read(ref current);
    public bool IsInstalled => Current.IsInstalled;
    public bool IsReady => ready.Task.IsCompletedSuccessfully;

    /// <summary>等待已安装实例通过启动数据检查。</summary>
    public Task WaitForReadyAsync(CancellationToken cancellationToken) => ready.Task.WaitAsync(cancellationToken);

    /// <summary>在同一已发布实例完成迁移后开放业务。</summary>
    public void MarkReady(InstanceStorageContext instance)
    {
        if (!instance.IsInstalled || !ReferenceEquals(Current, instance))
        {
            throw new InvalidOperationException("Only the published installation can become ready.");
        }
        ready.TrySetResult();
    }

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
