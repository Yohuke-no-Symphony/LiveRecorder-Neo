namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>受单次安装令牌保护的初始化。</summary>
public interface IInstallationService
{
    /// <summary>是否已经完成安装。</summary>
    bool IsInstalled { get; }
    /// <summary>验证部署参数、创建管理员并封闭安装入口。</summary>
    Task InstallAsync(InstallRequest request, CancellationToken cancellationToken = default);
}
