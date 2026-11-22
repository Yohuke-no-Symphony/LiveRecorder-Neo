using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>只恢复已保存的 AI 发布和租约状态，不在主站调用模型。</summary>
public sealed class AiJobMaintenanceWorker(IBootstrapStore bootstrap, AiJobExecutionService execution, AiConnectionTestCoordinator probes,
    ILogger<AiJobMaintenanceWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (bootstrap.IsInstalled)
                {
                    probes.Maintain();
                    await execution.MaintainAsync(stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("AI job state recovery will be retried: {ErrorType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
