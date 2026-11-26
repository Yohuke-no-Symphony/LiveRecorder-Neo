using Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

namespace Yohuke.LiveRecorder.Identity.Tests;

[Migration("0.2", "0.3")]
internal sealed class SecondTestMigration(MigrationTestProbe probe) : IMigration
{
    public Task PrepareAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        probe.Phases.Enqueue("0.3 prepare");
        probe.SecondCheckpoint = context.Checkpoint;
        return Task.CompletedTask;
    }

    public Task MigrateAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        probe.Phases.Enqueue("0.3 migrate");
        return Task.CompletedTask;
    }

    public Task VerifyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        probe.Phases.Enqueue("0.3 verify");
        return Task.CompletedTask;
    }
}
