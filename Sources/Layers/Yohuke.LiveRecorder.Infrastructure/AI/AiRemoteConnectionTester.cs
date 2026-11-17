using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>把管理页面的模型连接测试委派给已注册的独立 AI 节点。</summary>
public sealed class AiRemoteConnectionTester(AiJobScheduler scheduler) : IAiConnectionTester
{
    /// <inheritdoc />
    public Task<AiConnectionTestResult> TestAsync(AiSettings settings, string apiKey, CancellationToken cancellationToken) =>
        scheduler.TestConnectionAsync(settings, apiKey, cancellationToken);
}
