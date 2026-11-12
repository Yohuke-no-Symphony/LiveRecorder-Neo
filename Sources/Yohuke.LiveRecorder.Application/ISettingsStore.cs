using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application;

/// <summary>读取与更新管理设置。</summary>
public interface ISettingsStore
{
    /// <summary>读取当前设置。</summary>
    Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default);
    /// <summary>持久化经过校验的设置。</summary>
    Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default);
}
