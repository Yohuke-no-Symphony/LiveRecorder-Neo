using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证迁移器发现、持久化版本与故障续跑。</summary>
public sealed class MigrationManagerTests
{
    [Fact]
    public void RegistrationRejectsMissingAndDuplicateIntervals()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddMigrations(new(0, 1), new(0, 3), [typeof(SecondTestMigration)]));
        Assert.Throws<InvalidOperationException>(() =>
            services.AddMigrations(new(0, 1), new(0, 3), [typeof(FirstTestMigration), typeof(FirstTestMigration), typeof(SecondTestMigration)]));
    }

    [MongoFact]
    public Task UnversionedDatabaseRunsBothIntervalsInVersionOrder() => WithDatabaseAsync(async (database, paths) =>
    {
        var probe = new MigrationTestProbe();
        await using var services = CreateServices(probe);
        var manager = services.GetRequiredService<MigrationManager>();
        Assert.Null(await manager.GetVersionAsync(database));

        await manager.RunAsync(database, paths);
        await manager.RunAsync(database, paths);

        Assert.Equal("0.3", await manager.GetVersionAsync(database));
        Assert.Equal(["0.2 prepare", "0.2 migrate", "0.2 verify", "0.3 prepare", "0.3 migrate", "0.3 verify"], probe.Phases);
        Assert.Equal("0.1", probe.FirstSourceVersion);
        Assert.Same(database, probe.FirstDatabase);
        Assert.Same(paths, probe.FirstPaths);
        Assert.Null(probe.SecondCheckpoint);
        var state = await ReadStateAsync(database);
        Assert.False(state.Contains("PendingTarget"));
        Assert.False(state.Contains("Checkpoint"));
    });

    [MongoFact]
    public Task VerificationFailureRetainsCheckpointForAnotherManager() => WithDatabaseAsync(async (database, paths) =>
    {
        var failing = new MigrationTestProbe { FailVerification = true };
        await using (var services = CreateServices(failing))
        {
            var manager = services.GetRequiredService<MigrationManager>();
            await Assert.ThrowsAsync<IOException>(() => manager.RunAsync(database, paths));
            Assert.Equal("0.1", await manager.GetVersionAsync(database));
        }
        var interrupted = await ReadStateAsync(database);
        Assert.Equal("0.2", interrupted["PendingTarget"].AsString);
        Assert.Equal(MigrationTestProbe.CHECKPOINT, interrupted["Checkpoint"].AsString);
        Assert.False(interrupted.Contains("LeaseId"));

        var resumed = new MigrationTestProbe();
        await using var restarted = CreateServices(resumed);
        var retry = restarted.GetRequiredService<MigrationManager>();
        await retry.RunAsync(database, paths);

        Assert.Equal(MigrationTestProbe.CHECKPOINT, resumed.FirstCheckpoint);
        Assert.False(resumed.WroteCheckpoint);
        Assert.Null(resumed.SecondCheckpoint);
        Assert.Equal("0.3", await retry.GetVersionAsync(database));
    });

    [MongoFact]
    public Task FutureDataVersionCannotBeReplacedByOlderApplication() => WithDatabaseAsync(async (database, paths) =>
    {
        var future = new BsonDocument { { "_id", "server" }, { "Version", "9.0" } };
        await State(database).InsertOneAsync(future);
        var probe = new MigrationTestProbe();
        await using var services = CreateServices(probe);
        var manager = services.GetRequiredService<MigrationManager>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunAsync(database, paths));

        Assert.Equal(future, await ReadStateAsync(database));
        Assert.Empty(probe.Phases);
    });

    [MongoFact]
    public Task EmptyNewDatabaseStartsAtCurrentVersion() => WithDatabaseAsync(async (database, paths) =>
    {
        var probe = new MigrationTestProbe();
        await using var services = CreateServices(probe);
        var manager = services.GetRequiredService<MigrationManager>();

        await manager.InitializeNewAsync(database, paths);
        await manager.RunAsync(database, paths);

        Assert.Equal("0.3", await manager.GetVersionAsync(database));
        Assert.Empty(probe.Phases);
    });

    [MongoFact]
    public Task TwoManagersMigrateEachIntervalOnce() => WithDatabaseAsync(async (database, paths) =>
    {
        var probe = new MigrationTestProbe { PauseMigration = true };
        await using var first = CreateServices(probe);
        await using var second = CreateServices(probe);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = first.GetRequiredService<MigrationManager>().RunAsync(database, paths, timeout.Token);
        var contender = Task.CompletedTask;
        try
        {
            await probe.Entered.Task.WaitAsync(timeout.Token);
            contender = second.GetRequiredService<MigrationManager>().RunAsync(database, paths, timeout.Token);
            await Task.WhenAny(contender, Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token));
            Assert.False(contender.IsCompleted);
        }
        finally
        {
            probe.Release.TrySetResult();
            await Task.WhenAll(initial, contender).WaitAsync(timeout.Token);
        }

        Assert.Equal(["0.2 prepare", "0.2 migrate", "0.2 verify", "0.3 prepare", "0.3 migrate", "0.3 verify"], probe.Phases);
        Assert.Equal("0.3", (await ReadStateAsync(database))["Version"].AsString);
    });

    private static ServiceProvider CreateServices(MigrationTestProbe probe) => new ServiceCollection()
        .AddLogging().AddSingleton(probe).AddMigrations(new(0, 1), new(0, 3), [typeof(SecondTestMigration), typeof(FirstTestMigration)])
        .BuildServiceProvider();

    private static IMongoCollection<BsonDocument> State(IMongoDatabase database) => database.GetCollection<BsonDocument>("schema_migrations");

    private static Task<BsonDocument> ReadStateAsync(IMongoDatabase database) => State(database)
        .Find(new BsonDocument("_id", "server")).SingleAsync();

    private static async Task WithDatabaseAsync(Func<IMongoDatabase, IAppPaths, Task> runAsync)
    {
        var client = new MongoClient(Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")!);
        var name = "migration_tests_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "live-recorder-migration-tests", name);
        try
        {
            await runAsync(client.GetDatabase(name), new AppPaths(Path.Combine(directory, "data"), Path.Combine(directory, "records")));
        }
        finally
        {
            await client.DropDatabaseAsync(name);
        }
    }
}
