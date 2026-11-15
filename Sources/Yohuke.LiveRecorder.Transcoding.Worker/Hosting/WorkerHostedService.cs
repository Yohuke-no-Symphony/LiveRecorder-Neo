using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

internal sealed class WorkerHostedService(string configurationPath, IHostApplicationLifetime lifetime,
    ILogger<WorkerHostedService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WorkerApplication.RunAsync(configurationPath, stoppingToken,
                message => logger.LogInformation("{WorkerMessage}", message));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The worker service stopped after a fatal error.");
            Environment.Exit(1);
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
