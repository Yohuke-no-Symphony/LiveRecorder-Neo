using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Playback;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>周期处理保留期限与可回收播放缓存。</summary>
public sealed class StorageMaintenanceWorker(IBootstrapStore bootstrap, StorageService storage, SessionTrashService sessionTrash, SessionSegmentTrashService segmentTrash, PlaybackService playback, ILogger<StorageMaintenanceWorker> logger) : BackgroundService
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
                    await storage.MaintainAsync(stoppingToken);
                    await segmentTrash.MaintainAsync(stoppingToken);
                    await sessionTrash.MaintainAsync(stoppingToken);
                    await playback.MaintainAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("存储维护将重试：{Message}", error.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
