using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

/// <summary>以不可变保留起点及数据库共享策略协调日志TTL，支持永久保留和中断恢复。</summary>
public sealed class TaskLogRetentionPolicy(IMongoContext context, ISettingsStore? settings = null, TimeProvider? timeProvider = null) : IDisposable
{
    private const string POLICY_ID = "task-logs";
    private const string BATCHES = "transcodeWorkerLogBatches";
    private const string TTL_INDEX = "task_log_retention";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime nextVerificationUtc;
    private long verifiedRevision = -1;
    private IMongoDatabase? verifiedDatabase;
    private IMongoCollection<TaskLogRetentionState> Policies => context.Collection<TaskLogRetentionState>("taskLogRetentionPolicies").WithReadPreference(ReadPreference.Primary);

    /// <summary>初始化缺省策略并恢复尚未完成的基准迁移及TTL变更。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default) => await ReadAsync(cancellationToken);

    /// <summary>显式发布新的保留天数；设置保存入口应使用持久意图协调服务。</summary>
    public async Task ApplyAsync(int days, CancellationToken cancellationToken = default)
    {
        var revision = await RevisionAsync(cancellationToken);
        await PublishAsync(new() { Days = days, ExpectedRevision = revision }, cancellationToken);
    }

    /// <summary>读取用于设置变更CAS的共享版本。</summary>
    public async Task<long> RevisionAsync(CancellationToken cancellationToken = default) => (await ReadAsync(cancellationToken)).Revision;

    /// <summary>发布同一操作可重试的持久意图，陈旧意图不会覆盖已更新的共享策略。</summary>
    public async Task<int> PublishAsync(TaskLogRetentionChange change, CancellationToken cancellationToken = default)
    {
        if (change.Days is < 0 or > 3650)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await EnsureDocumentAsync(cancellationToken);
            if (state.ChangeId != change.ChangeId
                && (change.ExpectedRevision is { } expected ? state.Revision == expected : state.ChangedAtUtc <= change.RequestedAtUtc))
            {
                var now = clock.GetUtcNow().UtcDateTime;
                var floor = SnapshotCutoff(state, now);
                if (change.Days > 0 && now.AddDays(-change.Days) > floor)
                {
                    floor = now.AddDays(-change.Days);
                }
                await Policies.UpdateOneAsync(value => value.Id == POLICY_ID && value.Revision == state.Revision,
                    Builders<TaskLogRetentionState>.Update.Set(value => value.Days, change.Days).Inc(value => value.Revision, 1)
                        .Set(value => value.ChangeId, change.ChangeId).Set(value => value.ChangedAtUtc, change.RequestedAtUtc)
                        .Set(value => value.SnapshotImportFloorUtc, floor), cancellationToken: cancellationToken);
            }
            return (await ReconcileAsync(cancellationToken)).Days;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>读取当前有效保留截止时间；空值表示永久保留。</summary>
    public async Task<DateTime?> CutoffAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadAsync(cancellationToken);
        return state.Days == 0 ? null : clock.GetUtcNow().UtcDateTime.AddDays(-state.Days);
    }

    /// <summary>历史快照不得重新导入此前已经过期的源事件。</summary>
    public async Task<bool> MayImportSnapshotAsync(DateTime timestampUtc, CancellationToken cancellationToken = default)
    {
        var state = await ReadAsync(cancellationToken);
        return timestampUtc > SnapshotCutoff(state, clock.GetUtcNow().UtcDateTime);
    }

    private async Task<TaskLogRetentionState> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await EnsureDocumentAsync(cancellationToken);
            var database = context.Database;
            if (state.AppliedRevision != state.Revision || !state.AnchorsMigrated || verifiedRevision != state.Revision
                || !ReferenceEquals(verifiedDatabase?.Client, database.Client)
                || verifiedDatabase?.DatabaseNamespace.DatabaseName != database.DatabaseNamespace.DatabaseName || clock.GetUtcNow().UtcDateTime >= nextVerificationUtc)
            {
                state = await ReconcileAsync(cancellationToken);
            }
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TaskLogRetentionState> EnsureDocumentAsync(CancellationToken cancellationToken)
    {
        var state = await Policies.Find(value => value.Id == POLICY_ID).FirstOrDefaultAsync(cancellationToken);
        if (state is not null)
        {
            return state;
        }
        var days = settings is null ? 7 : (await settings.GetAsync(cancellationToken)).TaskLogRetentionDays;
        var now = clock.GetUtcNow().UtcDateTime;
        try
        {
            await Policies.UpdateOneAsync(value => value.Id == POLICY_ID,
                Builders<TaskLogRetentionState>.Update.SetOnInsert(value => value.Id, POLICY_ID).SetOnInsert(value => value.Days, Math.Clamp(days, 0, 3650))
                    .SetOnInsert(value => value.Revision, 0).SetOnInsert(value => value.AnchorsMigrated, false)
                    .SetOnInsert(value => value.AppliedRevision, -1).SetOnInsert(value => value.SnapshotImportFloorUtc, now.AddDays(-7)),
                new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }
        return await Policies.Find(value => value.Id == POLICY_ID).SingleAsync(cancellationToken);
    }

    private async Task<TaskLogRetentionState> ReconcileAsync(CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        TaskLogRetentionState? state;
        while (true)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            state = await Policies.FindOneAndUpdateAsync(value => value.Id == POLICY_ID
                    && (value.LeaseOwner == null || value.LeaseUntilUtc <= now),
                Builders<TaskLogRetentionState>.Update.Set(value => value.LeaseOwner, owner).Set(value => value.LeaseUntilUtc, now.AddMinutes(2)),
                new() { ReturnDocument = ReturnDocument.After }, cancellationToken);
            if (state is not null)
            {
                break;
            }
            await Task.Delay(100, cancellationToken);
        }
        try
        {
            await DropIndexAsync("worker_log_expiration", cancellationToken);
            if (!state.AnchorsMigrated)
            {
                await MigrateAnchorsAsync(owner, cancellationToken);
                await Policies.UpdateOneAsync(value => value.Id == POLICY_ID && value.LeaseOwner == owner,
                    Builders<TaskLogRetentionState>.Update.Set(value => value.AnchorsMigrated, true), cancellationToken: cancellationToken);
            }
            while (true)
            {
                await RenewAsync(owner, cancellationToken);
                state = await Policies.Find(value => value.Id == POLICY_ID).SingleAsync(cancellationToken);
                await ConfigureIndexAsync(state.Days, cancellationToken);
                var applied = await Policies.UpdateOneAsync(value => value.Id == POLICY_ID && value.LeaseOwner == owner && value.Revision == state.Revision,
                    Builders<TaskLogRetentionState>.Update.Set(value => value.AppliedRevision, state.Revision), cancellationToken: cancellationToken);
                if (applied.MatchedCount == 1)
                {
                    verifiedRevision = state.Revision;
                    verifiedDatabase = context.Database;
                    nextVerificationUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(15);
                    return state;
                }
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await Policies.UpdateOneAsync(value => value.Id == POLICY_ID && value.LeaseOwner == owner,
                    Builders<TaskLogRetentionState>.Update.Unset(value => value.LeaseOwner).Unset(value => value.LeaseUntilUtc), cancellationToken: cleanup.Token);
            }
            catch (Exception error) when (error is MongoException or OperationCanceledException or TimeoutException)
            {
            }
        }
    }

    private async Task MigrateAnchorsAsync(string owner, CancellationToken cancellationToken)
    {
        var batches = context.Collection<BsonDocument>(BATCHES).WithReadPreference(ReadPreference.Primary);
        var missing = new BsonDocument("RetentionStartedAtUtc", BsonNull.Value);
        var received = new BsonDocument("$ifNull", new BsonArray { "$ReceivedAtUtc", "$CreatedAtUtc" });
        var source = new BsonDocument("$arrayElemAt", new BsonArray { "$Entries.Source", 0 });
        var timestamp = new BsonDocument("$arrayElemAt", new BsonArray { "$Entries.TimestampUtc", 0 });
        var anchor = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { source, "SessionSnapshot" }), new BsonDocument("$ifNull", new BsonArray { timestamp, received }), received
        });
        while (true)
        {
            var rows = await batches.Find(missing).Project(new BsonDocument("_id", 1)).Limit(1000).ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return;
            }
            await RenewAsync(owner, cancellationToken);
            await batches.UpdateManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(rows.Select(row => row["_id"])))),
                new PipelineUpdateDefinition<BsonDocument>(new[]
                {
                    new BsonDocument("$set", new BsonDocument("RetentionStartedAtUtc", anchor)), new BsonDocument("$unset", "ExpiresAtUtc")
                }), cancellationToken: cancellationToken);
        }
    }

    private async Task RenewAsync(string owner, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var renewed = await Policies.UpdateOneAsync(value => value.Id == POLICY_ID && value.LeaseOwner == owner && value.LeaseUntilUtc > now,
            Builders<TaskLogRetentionState>.Update.Set(value => value.LeaseUntilUtc, now.AddMinutes(2)), cancellationToken: cancellationToken);
        if (renewed.MatchedCount != 1)
        {
            throw new InfrastructureException("The task log retention policy is waiting for reconciliation.", 503);
        }
    }

    private async Task ConfigureIndexAsync(int days, CancellationToken cancellationToken)
    {
        if (days == 0)
        {
            await DropIndexAsync(TTL_INDEX, cancellationToken);
            return;
        }
        var seconds = checked(days * 86400);
        List<BsonDocument> indexes;
        try
        {
            using var cursor = await context.Collection<BsonDocument>(BATCHES).Indexes.ListAsync(cancellationToken);
            indexes = await cursor.ToListAsync(cancellationToken);
        }
        catch (MongoCommandException error) when (error.Code == 26)
        {
            indexes = [];
        }
        var existing = indexes.FirstOrDefault(value => value["name"] == TTL_INDEX);
        if (existing is null)
        {
            await context.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "createIndexes", BATCHES }, { "indexes", new BsonArray { new BsonDocument
                {
                    { "key", new BsonDocument("RetentionStartedAtUtc", 1) }, { "name", TTL_INDEX }, { "expireAfterSeconds", seconds }
                } } }, { "maxTimeMS", 30000 }
            }, cancellationToken: cancellationToken);
        }
        else if (existing.GetValue("expireAfterSeconds", -1).ToInt64() != seconds)
        {
            await context.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "collMod", BATCHES }, { "index", new BsonDocument { { "name", TTL_INDEX }, { "expireAfterSeconds", seconds } } }, { "maxTimeMS", 30000 }
            }, cancellationToken: cancellationToken);
        }
    }

    private async Task DropIndexAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await context.Collection<BsonDocument>(BATCHES).Indexes.DropOneAsync(name, cancellationToken);
        }
        catch (MongoCommandException error) when (error.Code is 26 or 27)
        {
        }
    }

    private static DateTime SnapshotCutoff(TaskLogRetentionState state, DateTime now)
    {
        var cutoff = state.Days == 0 ? DateTime.MinValue : now.AddDays(-state.Days);
        return cutoff > state.SnapshotImportFloorUtc ? cutoff : state.SnapshotImportFloorUtc;
    }

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();
}
