namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>读取实例安装状态，并持久化受保护的数据库连接配置。</summary>
public interface IBootstrapStore
{
    BootstrapConfiguration? Current { get; }
    bool IsInstalled { get; }
    bool IsReady => IsInstalled;
    string InstallationToken { get; }

    /// <summary>原子保存实例的安装阶段与连接参数。</summary>
    Task SaveAsync(BootstrapConfiguration configuration, CancellationToken cancellationToken = default);
}
