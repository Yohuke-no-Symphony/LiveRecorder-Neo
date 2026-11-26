using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

/// <summary>绑定当前迁移使用的数据库、路径及持久断点。</summary>
public sealed class MigrationContext
{
    private readonly Func<string, CancellationToken, Task> saveCheckpoint;
    private readonly CancellationToken migrationCancellation;

    internal MigrationContext(IMongoDatabase database, IAppPaths paths, string? checkpoint,
        Func<string, CancellationToken, Task> saveCheckpoint, CancellationToken migrationCancellation)
    {
        Database = database;
        Paths = paths;
        Checkpoint = checkpoint;
        this.saveCheckpoint = saveCheckpoint;
        this.migrationCancellation = migrationCancellation;
    }

    public IMongoDatabase Database { get; }
    public IAppPaths Paths { get; }
    public string? Checkpoint { get; private set; }

    /// <summary>在本批数据提交后保存续跑位置。</summary>
    public async Task SaveCheckpointAsync(string checkpoint, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(migrationCancellation, cancellationToken);
        await saveCheckpoint(checkpoint, linked.Token);
        Checkpoint = checkpoint;
    }
}
