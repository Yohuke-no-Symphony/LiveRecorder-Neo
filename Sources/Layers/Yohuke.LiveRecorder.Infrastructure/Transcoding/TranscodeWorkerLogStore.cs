using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>原子接收节点日志批次，恢复发布意图，并提供保留七天的稳定行游标。</summary>
public sealed class TranscodeWorkerLogStore(IMongoContext context, TimeProvider? timeProvider = null) : IDisposable
{
    internal const long MAX_CURSOR = 9007199254740991;
    internal const int MAX_ENTRIES = 64;
    internal const int MAX_MESSAGE_LENGTH = 2048;
    private const string BATCH_COLLECTION = "transcodeWorkerLogBatches";
    private const string HEAD_COLLECTION = "transcodeWorkerLogHeads";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SemaphoreSlim indexes = new(1, 1);
    private IMongoClient? indexedClient;
    private string? indexedDatabase;

    private IMongoCollection<TranscodeWorkerLogBatch> Batches =>
        context.Collection<TranscodeWorkerLogBatch>(BATCH_COLLECTION).WithReadPreference(ReadPreference.Primary);
    private IMongoCollection<TranscodeWorkerLogHead> Heads =>
        context.Collection<TranscodeWorkerLogHead>(HEAD_COLLECTION).WithReadPreference(ReadPreference.Primary);

    /// <summary>持久化完整冻结批次，发布游标确认后才返回可用于移除本地缓存的应答。</summary>
    public async Task<WorkerLogAcknowledgement> AppendAsync(string workerId, WorkerLogBatch batch, CancellationToken cancellationToken = default)
    {
        Validate(batch);
        batch = batch with { Entries = batch.Entries.Select(entry => entry with { }).ToList() };
        await EnsureIndexesAsync(cancellationToken);
        var gate = Gate(workerId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var saved = await SaveBatchAsync(workerId, batch, cancellationToken);
            var head = await GetOrCreateHeadAsync(workerId, cancellationToken);
            while (true)
            {
                head = await RecoverPendingAsync(head, cancellationToken);
                saved = await Batches.Find(item => item.Id == saved.Id).FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InfrastructureException("The worker log batch expired during publication. Retry the same batch.", 409);
                if (saved.CursorEnd > 0)
                {
                    if (saved.CursorEnd > head.CommittedCursor)
                    {
                        throw new InfrastructureException("The worker log publication state is inconsistent.", 503);
                    }
                    return new()
                    {
                        StreamId = batch.StreamId,
                        BatchId = batch.BatchId,
                        ThroughSequence = batch.Entries[^1].Sequence
                    };
                }
                var next = checked(head.CommittedCursor + saved.Entries.Count);
                if (next > MAX_CURSOR)
                {
                    throw new InfrastructureException("The worker log cursor exceeds the supported range.", 503);
                }
                var changed = await Heads.UpdateOneAsync(item => item.Id == workerId
                        && item.CommittedCursor == head.CommittedCursor && item.PendingBatchId == null,
                    Builders<TranscodeWorkerLogHead>.Update.Set(item => item.PendingBatchId, saved.Id).Set(item => item.PendingCursor, next),
                    cancellationToken: cancellationToken);
                RequireAcknowledged(changed);
                head = await ReadHeadAsync(workerId, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>首次读取最近日志，后续按已提交行游标分页，过期条目立即隐藏。</summary>
    public async Task<TranscodeWorkerLogPage> ReadAsync(string workerId, long? after, int take = 200,
        CancellationToken cancellationToken = default)
    {
        if (after is < 0 or > MAX_CURSOR || take is < 1 or > 500)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        if (!await context.Collection<TranscodeWorker>("transcodeWorkers").Find(item => item.Id == workerId).AnyAsync(cancellationToken))
        {
            throw new InfrastructureException("The worker does not exist.", 404);
        }
        await EnsureIndexesAsync(cancellationToken);
        var head = await Heads.Find(item => item.Id == workerId).FirstOrDefaultAsync(cancellationToken);
        var committed = head?.CommittedCursor ?? 0;
        if (committed is < 0 or > MAX_CURSOR || after > committed)
        {
            throw new InfrastructureException("The worker log cursor is invalid. Refresh the log view.", 409);
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var filter = Builders<TranscodeWorkerLogBatch>.Filter.Where(item => item.WorkerId == workerId
            && item.CursorEnd > 0 && item.CursorEnd <= committed && item.ExpiresAtUtc > now);
        if (after is { } cursor)
        {
            filter &= Builders<TranscodeWorkerLogBatch>.Filter.Gt(item => item.CursorEnd, cursor);
        }
        var options = new FindOptions<TranscodeWorkerLogBatch>
        {
            BatchSize = 4,
            Limit = take + 1,
            Sort = after.HasValue ? Builders<TranscodeWorkerLogBatch>.Sort.Ascending(item => item.CursorEnd)
                : Builders<TranscodeWorkerLogBatch>.Sort.Descending(item => item.CursorEnd)
        };
        using var batches = await Batches.FindAsync(filter, options, cancellationToken);
        var entries = new List<TranscodeWorkerLogEntry>(take + 1);
        while (entries.Count <= take && await batches.MoveNextAsync(cancellationToken))
        {
            foreach (var batch in batches.Current)
            {
                var rows = Project(batch);
                if (!after.HasValue)
                {
                    rows = rows.Reverse();
                }
                foreach (var entry in rows)
                {
                    if (after.HasValue && entry.Cursor <= after.Value)
                    {
                        continue;
                    }
                    entries.Add(entry);
                    if (entries.Count > take)
                    {
                        break;
                    }
                }
                if (entries.Count > take)
                {
                    break;
                }
            }
        }
        var hasMore = after.HasValue && entries.Count > take;
        if (entries.Count > take)
        {
            entries.RemoveAt(entries.Count - 1);
        }
        if (!after.HasValue)
        {
            entries.Reverse();
        }
        return new()
        {
            Entries = entries,
            NextCursor = entries.Count > 0 ? entries[^1].Cursor : committed,
            HasMore = hasMore
        };
    }

    /// <summary>建立游标查询及七天过期索引，失败后允许再次初始化。</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await indexes.WaitAsync(cancellationToken);
        try
        {
            var database = context.Database;
            if (ReferenceEquals(indexedClient, database.Client) && indexedDatabase == database.DatabaseNamespace.DatabaseName)
            {
                return;
            }
            await Batches.Indexes.CreateManyAsync([
                new(Builders<TranscodeWorkerLogBatch>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
                    new CreateIndexOptions { Name = "worker_log_expiration", ExpireAfter = TimeSpan.Zero }),
                new(Builders<TranscodeWorkerLogBatch>.IndexKeys.Ascending(item => item.WorkerId).Ascending(item => item.CursorEnd),
                    new CreateIndexOptions<TranscodeWorkerLogBatch>
                    {
                        Name = "worker_log_cursor", Unique = true,
                        PartialFilterExpression = Builders<TranscodeWorkerLogBatch>.Filter.Gt(item => item.CursorEnd, 0)
                    })
            ], cancellationToken);
            indexedClient = database.Client;
            indexedDatabase = database.DatabaseNamespace.DatabaseName;
        }
        finally
        {
            indexes.Release();
        }
    }

    internal static void Validate(WorkerLogBatch batch)
    {
        if (batch is null || !Guid.TryParseExact(batch.StreamId, "N", out _) || !Guid.TryParseExact(batch.BatchId, "N", out _)
            || batch.DroppedCount is < 0 or > MAX_CURSOR || batch.Entries is not { Count: > 0 and <= MAX_ENTRIES })
        {
            throw new InfrastructureException("The worker log batch is invalid.");
        }
        long previous = 0;
        foreach (var entry in batch.Entries)
        {
            if (entry is null || entry.Sequence is <= 0 or > MAX_CURSOR || previous != 0 && entry.Sequence != previous + 1
                || entry.TimestampUtc.Kind != DateTimeKind.Utc || entry.TimestampUtc == DateTime.MinValue
                || entry.Level is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical")
                || string.IsNullOrEmpty(entry.Message) || entry.Message.Length > MAX_MESSAGE_LENGTH
                || entry.JobId?.Length > 128 || entry.Attempt is <= 0 || entry.Stage?.Length > 128
                || entry.Progress is { } progress && (!double.IsFinite(progress) || progress is < 0 or > 100))
            {
                throw new InfrastructureException("The worker log entry is invalid.");
            }
            previous = entry.Sequence;
        }
    }

    internal static IEnumerable<TranscodeWorkerLogEntry> Project(TranscodeWorkerLogBatch batch)
    {
        if (batch.Entries.Count is < 1 or > MAX_ENTRIES || batch.CursorEnd < batch.Entries.Count || batch.CursorEnd > MAX_CURSOR)
        {
            throw new InfrastructureException("The stored worker log cursor range is invalid.", 503);
        }
        for (var index = 0; index < batch.Entries.Count; index++)
        {
            var entry = batch.Entries[index];
            yield return new()
            {
                Id = batch.Id + "-" + index.ToString(CultureInfo.InvariantCulture),
                WorkerId = batch.WorkerId,
                StreamId = batch.StreamId,
                Sequence = entry.Sequence,
                Cursor = batch.CursorEnd - batch.Entries.Count + index + 1,
                TimestampUtc = entry.TimestampUtc,
                ReceivedAtUtc = batch.ReceivedAtUtc,
                Level = entry.Level,
                Message = entry.Message,
                JobId = entry.JobId,
                Attempt = entry.Attempt,
                Stage = entry.Stage,
                Progress = entry.Progress,
                DroppedCount = index == 0 ? batch.DroppedCount : 0
            };
        }
    }

    private async Task<TranscodeWorkerLogBatch> SaveBatchAsync(string workerId, WorkerLogBatch batch, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var id = TranscodeAssetRegistry.Hash(workerId + ":" + batch.StreamId + ":" + batch.BatchId);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(batch, WorkerProtocol.JsonOptions)));
        var update = Builders<TranscodeWorkerLogBatch>.Update.SetOnInsert(item => item.Id, id)
            .SetOnInsert(item => item.WorkerId, workerId).SetOnInsert(item => item.StreamId, batch.StreamId)
            .SetOnInsert(item => item.BatchId, batch.BatchId).SetOnInsert(item => item.PayloadHash, hash)
            .SetOnInsert(item => item.DroppedCount, batch.DroppedCount).SetOnInsert(item => item.Entries, batch.Entries)
            .SetOnInsert(item => item.CursorEnd, 0).SetOnInsert(item => item.CreatedAtUtc, now)
            .SetOnInsert(item => item.ReceivedAtUtc, now).SetOnInsert(item => item.ExpiresAtUtc, now.AddDays(7));
        try
        {
            RequireAcknowledged(await Batches.UpdateOneAsync(item => item.Id == id, update,
                new UpdateOptions { IsUpsert = true }, cancellationToken));
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }
        var saved = await Batches.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("The worker log batch was not saved.", 503);
        if (saved.PayloadHash != hash || saved.WorkerId != workerId || saved.StreamId != batch.StreamId || saved.BatchId != batch.BatchId)
        {
            throw new InfrastructureException("A worker log batch cannot change after its first submission.", 409);
        }
        return saved;
    }

    private async Task<TranscodeWorkerLogHead> GetOrCreateHeadAsync(string workerId, CancellationToken cancellationToken)
    {
        try
        {
            RequireAcknowledged(await Heads.UpdateOneAsync(item => item.Id == workerId,
                Builders<TranscodeWorkerLogHead>.Update.SetOnInsert(item => item.Id, workerId)
                    .SetOnInsert(item => item.CommittedCursor, 0).SetOnInsert(item => item.PendingBatchId, null)
                    .SetOnInsert(item => item.PendingCursor, 0).SetOnInsert(item => item.CreatedAtUtc, clock.GetUtcNow().UtcDateTime),
                new UpdateOptions { IsUpsert = true }, cancellationToken));
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }
        return await ReadHeadAsync(workerId, cancellationToken);
    }

    private async Task<TranscodeWorkerLogHead> RecoverPendingAsync(TranscodeWorkerLogHead head, CancellationToken cancellationToken)
    {
        while (head.PendingBatchId is { } batchId)
        {
            if (head.PendingCursor <= head.CommittedCursor || head.PendingCursor > MAX_CURSOR)
            {
                throw new InfrastructureException("The pending worker log cursor is invalid.", 503);
            }
            var batch = await Batches.Find(item => item.Id == batchId && item.WorkerId == head.Id).FirstOrDefaultAsync(cancellationToken);
            if (batch is not null)
            {
                if (head.PendingCursor - head.CommittedCursor != batch.Entries.Count)
                {
                    throw new InfrastructureException("The pending worker log cursor range does not match its batch.", 503);
                }
                if (batch.CursorEnd != 0 && batch.CursorEnd != head.PendingCursor)
                {
                    throw new InfrastructureException("The pending worker log batch has conflicting cursor values.", 503);
                }
                var published = await Batches.UpdateOneAsync(item => item.Id == batchId
                        && (item.CursorEnd == 0 || item.CursorEnd == head.PendingCursor),
                    Builders<TranscodeWorkerLogBatch>.Update.Set(item => item.CursorEnd, head.PendingCursor), cancellationToken: cancellationToken);
                RequireAcknowledged(published);
                if (published.MatchedCount == 0)
                {
                    throw new InfrastructureException("The pending worker log batch disappeared during publication.", 409);
                }
            }
            var committed = await Heads.UpdateOneAsync(item => item.Id == head.Id && item.CommittedCursor == head.CommittedCursor
                    && item.PendingBatchId == batchId && item.PendingCursor == head.PendingCursor,
                Builders<TranscodeWorkerLogHead>.Update.Set(item => item.CommittedCursor, head.PendingCursor)
                    .Set(item => item.PendingBatchId, null).Set(item => item.PendingCursor, 0), cancellationToken: cancellationToken);
            RequireAcknowledged(committed);
            head = await ReadHeadAsync(head.Id, cancellationToken);
        }
        return head;
    }

    private Task<TranscodeWorkerLogHead> ReadHeadAsync(string workerId, CancellationToken cancellationToken) =>
        Heads.Find(item => item.Id == workerId).SingleAsync(cancellationToken);

    private SemaphoreSlim Gate(string workerId) => gates[(uint)StringComparer.Ordinal.GetHashCode(workerId) % (uint)gates.Length];

    private static void RequireAcknowledged(UpdateResult result)
    {
        if (!result.IsAcknowledged)
        {
            throw new InfrastructureException("MongoDB must acknowledge worker log writes before the batch can be accepted.", 503);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        indexes.Dispose();
        foreach (var gate in gates)
        {
            gate.Dispose();
        }
    }
}
