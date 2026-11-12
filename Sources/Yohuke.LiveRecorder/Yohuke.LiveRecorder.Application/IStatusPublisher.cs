namespace Yohuke.LiveRecorder.Application;

/// <summary>发布录制和任务状态变化。</summary>
public interface IStatusPublisher
{
    /// <summary>通知已获授权的客户端刷新状态。</summary>
    Task PublishAsync(string kind, string id, string? ownerId = null, CancellationToken cancellationToken = default);
}
