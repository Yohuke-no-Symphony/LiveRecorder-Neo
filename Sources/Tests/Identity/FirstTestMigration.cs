using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

namespace Yohuke.LiveRecorder.Identity.Tests;

[Migration("0.1", "0.2")]
internal sealed class FirstTestMigration(MigrationTestProbe probe) : IMigration
{
    public async Task PrepareAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        probe.Phases.Enqueue("0.2 prepare");
        probe.FirstCheckpoint = context.Checkpoint;
        probe.FirstDatabase = context.Database;
        probe.FirstPaths = context.Paths;
        var state = await context.Database.GetCollection<BsonDocument>("schema_migrations")
            .Find(new BsonDocument("_id", "server")).SingleAsync(cancellationToken);
        probe.FirstSourceVersion = state["Version"].AsString;
    }

    public async Task MigrateAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        probe.Phases.Enqueue("0.2 migrate");
        if (probe.PauseMigration)
        {
            probe.Entered.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
        }

        if (context.Checkpoint is null)
        {
            await context.SaveCheckpointAsync(MigrationTestProbe.CHECKPOINT, cancellationToken);
            probe.WroteCheckpoint = true;
        }
    }

    public Task VerifyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        probe.Phases.Enqueue("0.2 verify");
        return probe.FailVerification ? Task.FromException(new IOException("Simulated migration verification failure.")) : Task.CompletedTask;
    }
}
