using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

/// <summary>恢复日志保留的本地发布意图、共享待应用版本及实际TTL索引。</summary>
public sealed class TaskLogRetentionWorker(IBootstrapStore bootstrap, TaskLogRetentionPolicy retention,
    TaskLogRetentionSettingsService settings, ILogger<TaskLogRetentionWorker> logger) : BackgroundService
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
                    await settings.ReplayPendingAsync(stoppingToken);
                    await retention.InitializeAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Task log retention is waiting for reconciliation");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
