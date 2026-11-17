using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application;

/// <summary>读取与更新管理设置。</summary>
public interface ISettingsStore
{
    /// <summary>读取当前设置。</summary>
    Task<ServerSettings> GetAsync(CancellationToken cancellationToken = default);
    /// <summary>持久化经过校验的设置。</summary>
    Task SaveAsync(ServerSettings settings, CancellationToken cancellationToken = default);
    /// <summary>在同一次独占更新中读取最新设置、应用变更并返回保存后的快照。</summary>
    Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default);
}
