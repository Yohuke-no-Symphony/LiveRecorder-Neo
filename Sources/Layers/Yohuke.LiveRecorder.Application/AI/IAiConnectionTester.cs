using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>通过最小工具调用验证配置，不读取直播文本。</summary>
public interface IAiConnectionTester
{
    /// <summary>执行管理员显式触发的一次有界能力检查。</summary>
    Task<AiConnectionTestResult> TestAsync(AiSettings settings, string apiKey, CancellationToken cancellationToken);
}
