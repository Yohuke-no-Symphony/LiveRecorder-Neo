using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class MigrationStartupService(CurrentInstanceStorage storage, MigrationManager migrations, TimeProvider clock,
    IHostApplicationLifetime lifetime, ILogger<MigrationStartupService> logger) : BackgroundService, IHostedLifecycleService
{
    private bool waitForDatabase;
    private volatile bool failed;

    internal bool Failed => failed;

    async Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken)
    {
        var instance = storage.Current;
        if (!instance.IsInstalled)
        {
            return;
        }
        waitForDatabase = !await CanReadVersionAsync(instance, cancellationToken);
        if (waitForDatabase)
        {
            logger.LogWarning("Data migration is waiting for MongoDB. Only startup diagnostics are available.");
            return;
        }
        await OpenInstanceAsync(instance, cancellationToken);
    }

    Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!waitForDatabase)
        {
            return;
        }
        var instance = storage.Current;
        try
        {
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }
            while (!await CanReadVersionAsync(instance, stoppingToken));
            await OpenInstanceAsync(instance, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failed = true;
            logger.LogError(error, "Data migration failed after MongoDB became available. The server will stop.");
            lifetime.StopApplication();
        }
    }

    private async Task<bool> CanReadVersionAsync(InstanceStorageContext instance, CancellationToken cancellationToken)
    {
        try
        {
            await migrations.GetVersionAsync(instance.Mongo.Database, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is MongoConnectionException or TimeoutException)
        {
            return false;
        }
    }

    private async Task OpenInstanceAsync(InstanceStorageContext instance, CancellationToken cancellationToken)
    {
        await migrations.RunAsync(instance.Mongo.Database, instance.Paths, cancellationToken);
        storage.MarkReady(instance);
    }
}
