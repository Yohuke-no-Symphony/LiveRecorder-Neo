using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>定期检查采集凭据并清理过期登录流程。</summary>
public sealed class BilibiliAccountMaintenanceService(BilibiliService service, IBootstrapStore bootstrap, ILogger<BilibiliAccountMaintenanceService> logger) : BackgroundService
{
    /// <summary>独立执行凭据维护，失败不阻断录制服务启动。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            if (bootstrap.IsInstalled)
            {
                try
                {
                    await service.MaintainAccountsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Capture account maintenance is temporarily unavailable: {ErrorType}", exception.GetType().Name);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
