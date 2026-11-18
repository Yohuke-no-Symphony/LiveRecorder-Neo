using System.Globalization;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

/// <summary>统一查询业务任务，并独立清理个人或管理员的历史展示记录。</summary>
public sealed class TaskHistoryService(IMongoContext context, WorkerConnectionStatusTracker connections, IAppPaths paths)
{
    private static readonly HashSet<string> Kinds = ["media", "ai", "transcode", "recording"];
    private static readonly HashSet<string> States = ["all", "active", "failed", "Queued", "Running", "Publishing", "Completed", "Failed", "Cancelled", "Skipped", "Partial", "Interrupted"];

    /// <summary>批量投影每个节点最新的三条活动任务。</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<BackgroundTaskSummary>>> ActiveAsync(TaskHistoryActiveRequest request,
        TaskHistoryViewer viewer, CancellationToken cancellationToken)
    {
        Require(new(), viewer, true);
        if (request.WorkerIds is null || request.WorkerIds.Count > 256 || request.WorkerIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        if (request.WorkerIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<BackgroundTaskSummary>>();
        }
        var pipeline = TaskHistoryProjection.Build(viewer, true, new() { State = "active" });
        pipeline.Add(new("$match", new BsonDocument("WorkerId", new BsonDocument("$in", new BsonArray(request.WorkerIds.Distinct())))));
        pipeline.Add(TaskHistoryProjection.Sort());
        pipeline.Add(new("$group", new BsonDocument { { "_id", "$WorkerId" }, { "items", new BsonDocument("$push", "$$ROOT") } }));
        pipeline.Add(new("$project", new BsonDocument("items", new BsonDocument("$slice", new BsonArray { "$items", 3 }))));
        var groups = await Rows.Aggregate<BsonDocument>(pipeline, new AggregateOptions { AllowDiskUse = true }).ToListAsync(cancellationToken);
        return groups.ToDictionary(group => group["_id"].AsString,
            group => (IReadOnlyList<BackgroundTaskSummary>)group["items"].AsBsonArray.Select(row => TaskSummaryMapper.From(row.AsBsonDocument, viewer, connections)).ToArray(), StringComparer.Ordinal);
    }

    /// <summary>读取管理员筛选所需的公开姓名与节点名称。</summary>
    public async Task<TaskHistoryFilters> FiltersAsync(TaskHistoryViewer viewer, CancellationToken cancellationToken)
    {
        Require(new(), viewer, true);
        var initiators = await context.Collection<UserAccount>("users").Find(FilterDefinition<UserAccount>.Empty)
            .Project(user => new { user.Id, user.DisplayName }).ToListAsync(cancellationToken);
        var workers = await context.Collection<TranscodeWorker>("transcodeWorkers").Find(FilterDefinition<TranscodeWorker>.Empty)
            .Project(worker => new { worker.Id, worker.Name }).ToListAsync(cancellationToken);
        return new(initiators.Select(value => new TaskHistoryFilterOption(value.Id, value.DisplayName)).OrderBy(value => value.Name).ToArray(),
            workers.Select(value => new TaskHistoryFilterOption(value.Id, value.Name)).OrderBy(value => value.Name).ToArray());
    }

    /// <summary>在所有来源合并和权限过滤之后执行统一游标分页。</summary>
    public async Task<TaskHistoryPage> ListAsync(TaskHistoryQuery query, TaskHistoryViewer viewer, bool administration, CancellationToken cancellationToken)
    {
        Require(query, viewer, administration);
        var pipeline = TaskHistoryProjection.Build(viewer, administration, query);
        var page = new BsonArray();
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            var cursor = Decode(query.Cursor);
            page.Add(new BsonDocument("$match", new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("CreatedAtUtc", new BsonDocument("$lt", cursor.CreatedAtUtc)),
                new BsonDocument { { "CreatedAtUtc", cursor.CreatedAtUtc }, { "Kind", new BsonDocument("$lt", cursor.Kind) } },
                new BsonDocument { { "CreatedAtUtc", cursor.CreatedAtUtc }, { "Kind", cursor.Kind }, { "Id", new BsonDocument("$lt", cursor.Id) } }
            })));
        }
        page.Add(TaskHistoryProjection.Sort());
        page.Add(new BsonDocument("$limit", query.Limit + 1));
        pipeline.Add(new("$facet", new BsonDocument
        {
            { "items", page }, { "total", new BsonArray { new BsonDocument("$count", "value") } }
        }));
        var result = await Rows.Aggregate<BsonDocument>(pipeline, new AggregateOptions { AllowDiskUse = true }).FirstOrDefaultAsync(cancellationToken);
        var rows = result?["items"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray() ?? [];
        var items = rows.Take(query.Limit).Select(row => TaskSummaryMapper.From(row, viewer, connections)).ToArray();
        var total = result?["total"].AsBsonArray.FirstOrDefault()?.AsBsonDocument["value"].ToInt64() ?? 0;
        return new(items, total, rows.Length > query.Limit && items.Length > 0 ? Encode(items[^1]) : null);
    }

    /// <summary>读取同一权限范围内任务的阶段详情。</summary>
    public async Task<BackgroundTaskSummary> GetAsync(string kind, string id, TaskHistoryViewer viewer, bool administration, CancellationToken cancellationToken)
    {
        RequireIdentity(kind, id);
        var query = new TaskHistoryQuery { Kind = kind };
        Require(query, viewer, administration);
        var pipeline = TaskHistoryProjection.Build(viewer, administration, query, id);
        pipeline.Add(new("$limit", 1));
        var row = await Rows.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException(MessageCode.REQUEST_ENDPOINT_NOT_FOUND, 404);
        return TaskSummaryMapper.From(row, viewer, connections);
    }

    /// <summary>由管理员确认缓存丢失后结束历史任务；较新的场次进度使确认失效。</summary>
    public async Task InterruptRecordingAsync(string id, RecordingTaskInterruptRequest request, TaskHistoryViewer viewer, CancellationToken cancellationToken)
    {
        RequireIdentity("recording", id);
        Require(new(), viewer, true);
        if (request.LastReportedAtUtc == default || request.LastReportedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        var snapshots = context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks");
        var snapshot = await snapshots.Find(value => value.Id == id).FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.WorkerId)
            || Milliseconds(snapshot.LastReportedAtUtc) != Milliseconds(request.LastReportedAtUtc))
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }

        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, snapshot.WorkerId, cancellationToken);
        var task = await GetAsync("recording", id, viewer, true, cancellationToken);
        if (!task.CanInterruptRecording || task.WorkerId != snapshot.WorkerId || task.LastUpdatedAtUtc != snapshot.LastReportedAtUtc)
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }

        await context.Collection<RecordingTaskInterruption>("recordingTaskInterruptions").ReplaceOneAsync(value => value.Id == id, new()
        {
            Id = id, WorkerId = snapshot.WorkerId, AssignmentId = snapshot.AssignmentId, Generation = snapshot.Generation,
            LastReportedAtUtc = snapshot.LastReportedAtUtc, InterruptedByUserId = viewer.UserId
        }, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>仅隐藏确认时仍为相同终态的记录。</summary>
    public async Task DeleteAsync(string kind, string id, TaskHistoryDeleteRequest request, TaskHistoryViewer viewer, bool administration, CancellationToken cancellationToken)
    {
        var task = await GetAsync(kind, id, viewer, administration, cancellationToken);
        if (!task.CanDelete || task.FinishedAtUtc is not { } finished || task.Attempt != request.Attempt
            || (kind == "recording" || request.State is not null) && task.State != request.State
            || Milliseconds(finished) != Milliseconds(request.FinishedAtUtc))
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }
        var entry = Dismissal(task, viewer, administration);
        await Dismissals.ReplaceOneAsync(value => value.Id == entry.Id, entry, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>预览当前筛选下全部分页中的成功记录数量。</summary>
    public async Task<TaskHistoryClearPreview> PreviewClearAsync(TaskHistoryQuery query, TaskHistoryViewer viewer, bool administration, CancellationToken cancellationToken)
    {
        Require(query, viewer, administration);
        var cutoff = DateTime.UtcNow;
        var pipeline = Completed(query, viewer, administration, cutoff);
        pipeline.Add(new("$count", "value"));
        var count = await Rows.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync(cancellationToken);
        return new(count?.GetValue("value", 0).ToInt64() ?? 0, cutoff);
    }

    /// <summary>清理确认截止时间之前仍成功的记录，保留任务实体和所有产物。</summary>
    public async Task<long> ClearCompletedAsync(TaskHistoryClearRequest request, TaskHistoryViewer viewer, bool administration, CancellationToken cancellationToken)
    {
        if (request.Filter is null)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        Require(request.Filter, viewer, administration);
        if (request.CompletedBeforeUtc == default || request.CompletedBeforeUtc > DateTime.UtcNow.AddMinutes(1))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        var pipeline = Completed(request.Filter, viewer, administration, request.CompletedBeforeUtc);
        pipeline.Add(new("$project", new BsonDocument
        {
            { "Data", 0 }, { "InitiatorAvatarUrl", 0 }
        }));
        using var cursor = await Rows.AggregateAsync<BsonDocument>(pipeline, new AggregateOptions { BatchSize = 128, AllowDiskUse = true }, cancellationToken);
        var changed = 0L;
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            var entries = cursor.Current.Select(row => Dismissal(TaskSummaryMapper.From(row, viewer, connections), viewer, administration)).ToArray();
            if (entries.Length == 0)
            {
                continue;
            }
            var result = await Dismissals.BulkWriteAsync(entries.Select(entry => new ReplaceOneModel<TaskHistoryDismissal>(
                Builders<TaskHistoryDismissal>.Filter.Eq(value => value.Id, entry.Id), entry) { IsUpsert = true }),
                new BulkWriteOptions { IsOrdered = false }, cancellationToken);
            changed += result.Upserts.Count;
        }
        return changed;
    }

    private List<BsonDocument> Completed(TaskHistoryQuery query, TaskHistoryViewer viewer, bool administration, DateTime cutoff)
    {
        var pipeline = TaskHistoryProjection.Build(viewer, administration, query with { Cursor = null });
        pipeline.Add(new("$match", new BsonDocument
        {
            { "State", "Completed" }, { "FinishedAtUtc", new BsonDocument("$lte", cutoff) }
        }));
        return pipeline;
    }

    private static TaskHistoryDismissal Dismissal(BackgroundTaskSummary task, TaskHistoryViewer viewer, bool administration)
    {
        var finished = task.FinishedAtUtc!.Value;
        var prefix = administration ? "Admin:" : "User:" + viewer.UserId + ":";
        var suffix = task is { Kind: "recording", State: "Interrupted" } ? TaskHistoryProjection.INTERRUPTED_RECORDING_DISMISSAL_SUFFIX : "";
        return new()
        {
            Id = prefix + task.Kind + ":" + task.Id + ":" + task.Attempt.ToString(CultureInfo.InvariantCulture) + ":" + Milliseconds(finished).ToString(CultureInfo.InvariantCulture) + suffix,
            Scope = administration ? "Admin" : "User", ViewerId = administration ? "" : viewer.UserId,
            Kind = task.Kind, TaskId = task.Id, Attempt = task.Attempt, FinishedAtUtc = finished
        };
    }

    private static void Require(TaskHistoryQuery query, TaskHistoryViewer viewer, bool administration)
    {
        if (administration && !viewer.IsAdmin)
        {
            throw new InfrastructureException(MessageCode.REQUEST_FORBIDDEN, 403);
        }
        if (query.Limit is < 1 or > 200 || query.Search?.Length > 256 || query.Cursor?.Length > 1024
            || query.Kind is { Length: > 0 } kind && !Kinds.Contains(kind)
            || query.State is { Length: > 0 } state && !States.Contains(state)
            || query.Origin is { Length: > 0 } origin && origin is not ("User" or "Automatic" or "Legacy")
            || query.Type?.Length > 64 || query.WorkerId?.Length > 128 || query.InitiatorUserId?.Length > 128)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
    }

    private static void RequireIdentity(string kind, string id)
    {
        if (!Kinds.Contains(kind) || id.Length is < 1 or > 128 || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
    }

    private static TaskHistoryCursor Decode(string cursor)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TaskHistoryCursor>(Convert.FromBase64String(cursor));
            if (value is null || !Kinds.Contains(value.Kind) || string.IsNullOrEmpty(value.Id) || value.Id.Length > 128)
            {
                throw new FormatException();
            }
            return value;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
    }

    private static string Encode(BackgroundTaskSummary task) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new TaskHistoryCursor(task.CreatedAtUtc, task.Kind, task.Id)));
    private static long Milliseconds(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private IMongoCollection<BsonDocument> Rows => context.Collection<BsonDocument>("jobs");
    private IMongoCollection<TaskHistoryDismissal> Dismissals => context.Collection<TaskHistoryDismissal>("taskHistoryDismissals");
}
