namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>转发本地安装配置，实例就绪状态始终由已发布快照确定，不随配置文件写入改变。</summary>
public sealed class CurrentBootstrapStore(CurrentInstanceStorage storage) : IBootstrapStore
{
    public BootstrapConfiguration? Current => storage.Current.Bootstrap.Current;
    public bool IsInstalled => storage.IsInstalled;
    public string InstallationToken => storage.Current.Bootstrap.InstallationToken;

    /// <inheritdoc />
    public Task SaveAsync(BootstrapConfiguration configuration, CancellationToken cancellationToken = default)
    {
        return storage.Current.Bootstrap.SaveAsync(configuration, cancellationToken);
    }
}
