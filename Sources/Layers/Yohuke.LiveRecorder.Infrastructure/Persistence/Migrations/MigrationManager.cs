using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

/// <summary>按已提交的数据版本逐段运行和恢复实例迁移。</summary>
public sealed class MigrationManager
{
    private readonly MigrationCatalog catalog;
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider clock;
    private readonly ILogger<MigrationManager> logger;

    internal MigrationManager(MigrationCatalog catalog, IServiceScopeFactory scopes, TimeProvider clock, ILogger<MigrationManager> logger)
    {
        this.catalog = catalog;
        this.scopes = scopes;
        this.clock = clock;
        this.logger = logger;
    }

    /// <summary>读取已提交的数据版本，空值表示旧库没有版本标记。</summary>
    public async Task<string?> GetVersionAsync(IMongoDatabase database, CancellationToken cancellationToken = default) =>
        (await new MigrationStateStore(database).ReadAsync(cancellationToken))?.Version;

    /// <summary>标记新装实例的数据版本，并恢复已部分提交的新装。</summary>
    public async Task InitializeNewAsync(IMongoDatabase database, IAppPaths paths, CancellationToken cancellationToken = default)
    {
        await new MigrationStateStore(database).InitializeAsync(catalog.Target, cancellationToken);
        await RunAsync(database, paths, cancellationToken);
    }

    /// <summary>在指定的数据库和实例目录上执行所有待迁移区间。</summary>
    public async Task RunAsync(IMongoDatabase database, IAppPaths paths, CancellationToken cancellationToken = default)
    {
        var store = new MigrationStateStore(database);
        var leaseId = Guid.NewGuid().ToString("N");
        while (true)
        {
            var state = await store.ReadAsync(cancellationToken);
            if (state is null)
            {
                await store.InitializeAsync(catalog.Legacy, cancellationToken);
                continue;
            }
            if (catalog.GetNext(state.Version, state.PendingTarget) is null)
            {
                return;
            }
            var acquired = await store.AcquireAsync(state.Version, leaseId, cancellationToken);
            if (acquired is not null)
            {
                await RunUnderLeaseAsync(store, acquired, database, paths, leaseId, cancellationToken);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, cancellationToken);
        }
    }

    private async Task RunUnderLeaseAsync(MigrationStateStore store, MigrationState state, IMongoDatabase database,
        IAppPaths paths, string leaseId, CancellationToken cancellationToken)
    {
        using var leaseLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseAsync(store, leaseId, leaseLifetime);
        try
        {
            while (catalog.GetNext(state.Version, state.PendingTarget) is { } step)
            {
                if (state.PendingTarget is null)
                {
                    await store.BeginAsync(step, leaseId, leaseLifetime.Token);
                }
                await RunStepAsync(step, store, state.Checkpoint, database, paths, leaseId, leaseLifetime.Token);
                state.Version = step.To;
                state.PendingTarget = null;
                state.Checkpoint = null;
            }
        }
        finally
        {
            await leaseLifetime.CancelAsync();
            try
            {
                await renewal;
            }
            finally
            {
                await ReleaseLeaseAsync(store, leaseId);
            }
        }
    }

    private async Task RunStepAsync(MigrationStep step, MigrationStateStore store, string? checkpoint,
        IMongoDatabase database, IAppPaths paths, string leaseId, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var phase = nameof(IMigration.PrepareAsync);
        logger.LogInformation("Starting data migration {From} to {To}.", step.From, step.To);
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var migration = (IMigration)scope.ServiceProvider.GetRequiredService(step.Implementation);
                var context = new MigrationContext(database, paths, checkpoint,
                    (cursor, token) => store.SaveCheckpointAsync(step, leaseId, cursor, token), cancellationToken);
                await migration.PrepareAsync(context, cancellationToken);
                phase = nameof(IMigration.MigrateAsync);
                await migration.MigrateAsync(context, cancellationToken);
                phase = nameof(IMigration.VerifyAsync);
                await migration.VerifyAsync(context, cancellationToken);
            }
            phase = "Commit";
            await store.CommitAsync(step, leaseId, cancellationToken);
            logger.LogInformation("Completed data migration {From} to {To} in {Elapsed}.", step.From, step.To, clock.GetElapsedTime(started));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(error, "Data migration {From} to {To} failed in {Phase}.", step.From, step.To, phase);
            throw;
        }
    }

    private async Task RenewLeaseAsync(MigrationStateStore store, string leaseId, CancellationTokenSource leaseLifetime)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), clock, leaseLifetime.Token);
                await store.RenewAsync(leaseId, leaseLifetime.Token);
            }
        }
        catch (OperationCanceledException) when (leaseLifetime.IsCancellationRequested)
        {
        }
        catch
        {
            await leaseLifetime.CancelAsync();
            throw;
        }
    }

    private async Task ReleaseLeaseAsync(MigrationStateStore store, string leaseId)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await store.ReleaseAsync(leaseId, cleanup.Token);
        }
        catch (Exception error) when (error is MongoException or TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Data migration lease could not be released ({ErrorType}).", error.GetType().Name);
        }
    }
}
