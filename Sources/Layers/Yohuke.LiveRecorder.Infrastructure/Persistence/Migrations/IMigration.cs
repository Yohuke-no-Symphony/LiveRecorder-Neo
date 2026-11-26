namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

/// <summary>可在中断后重跑的数据迁移生命周期。</summary>
public interface IMigration
{
    /// <summary>检查迁移所需的前置条件。</summary>
    Task PrepareAsync(MigrationContext context, CancellationToken cancellationToken);

    /// <summary>迁移数据，完成一批数据后可保存续跑位置。</summary>
    Task MigrateAsync(MigrationContext context, CancellationToken cancellationToken);

    /// <summary>在提交目标版本前核验数据。</summary>
    Task VerifyAsync(MigrationContext context, CancellationToken cancellationToken);
}
