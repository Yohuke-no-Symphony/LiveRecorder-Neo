using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Tasks;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证实际Mongo索引切换、旧日志迁移及持久策略意图重放。</summary>
public sealed class TaskLogRetentionPolicyHttpTests
{
    /// <summary>设置热变更作用于尚存日志，不续期、不重置游标，0移除TTL。</summary>
    [MongoHttpFact]
    public async Task ExistingBatchesFollowChangedRetentionWithoutRenewingTheirAnchor()
    {
        await using var fixture = await FixtureAsync();
        var logs = fixture.App.Services.GetRequiredService<TranscodeWorkerLogStore>();
        var policy = fixture.App.Services.GetRequiredService<TaskLogRetentionPolicy>();
        var started = Milliseconds(DateTime.UtcNow.AddDays(-10));
        var batch = Batch("entry", 1);
        await logs.AppendAsync("worker", batch, retentionStartedAtUtc: started);
        var original = await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Find(value => value.WorkerId == "worker").SingleAsync();
        var hidden = await logs.ReadTaskAsync("worker", "job", 1, "Encoding", null);
        Assert.Empty(hidden.Entries);
        await policy.ApplyAsync(30);
        Assert.Equal("entry", Assert.Single((await logs.ReadTaskAsync("worker", "job", 1, "Encoding", null)).Entries).Message);
        await logs.AppendAsync("worker", batch, retentionStartedAtUtc: DateTime.UtcNow);
        var replayed = await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Find(value => value.Id == original.Id).SingleAsync();
        Assert.Equal(started, replayed.RetentionStartedAtUtc);
        Assert.Equal(original.ReceivedAtUtc, replayed.ReceivedAtUtc);
        Assert.Equal(original.CursorEnd, replayed.CursorEnd);
        await policy.ApplyAsync(1);
        Assert.Empty((await logs.ReadTaskAsync("worker", "job", 1, "Encoding", null)).Entries);
        await policy.ApplyAsync(0);
        Assert.Single((await logs.ReadTaskAsync("worker", "job", 1, "Encoding", null)).Entries);
        Assert.DoesNotContain(await IndexesAsync(fixture), index => index.GetValue("expireAfterSeconds", BsonNull.Value) != BsonNull.Value);
        await policy.ApplyAsync(3650);
        var ttl = Assert.Single(await IndexesAsync(fixture), index => index["name"] == "task_log_retention");
        Assert.Equal(315360000, ttl["expireAfterSeconds"].ToInt64());
    }

    /// <summary>旧运行日志按接收时间保留，历史场次事件按结束时间保留，旧TTL先移除。</summary>
    [MongoHttpFact]
    public async Task MigrationPreservesReceiptAndSnapshotEventAnchors()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Context.Collection<BsonDocument>("taskLogRetentionPolicies").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        await DropNewTtlAsync(fixture);
        var received = Milliseconds(DateTime.UtcNow.AddDays(-1));
        var ended = Milliseconds(DateTime.UtcNow.AddDays(-2));
        await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").InsertManyAsync([
            new() { Id = "legacy-worker", WorkerId = "worker", ReceivedAtUtc = received, ExpiresAtUtc = received.AddDays(7),
                Entries = [new() { Sequence = 1, TimestampUtc = received.AddYears(-1), Message = "Worker time is not a retention anchor." }] },
            new() { Id = "legacy-snapshot", WorkerId = "local", ReceivedAtUtc = received, ExpiresAtUtc = ended.AddDays(7),
                Entries = [new() { Sequence = 1, TimestampUtc = ended, Source = "SessionSnapshot", Message = "Historical event." }] }
        ]);
        await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Indexes.CreateOneAsync(new CreateIndexModel<TranscodeWorkerLogBatch>(
            Builders<TranscodeWorkerLogBatch>.IndexKeys.Ascending(value => value.ExpiresAtUtc), new CreateIndexOptions { Name = "worker_log_expiration", ExpireAfter = TimeSpan.Zero }));
        using var policy = new TaskLogRetentionPolicy(fixture.Context);
        await policy.InitializeAsync();
        var rows = await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Find(FilterDefinition<TranscodeWorkerLogBatch>.Empty).ToListAsync();
        Assert.Equal(received, rows.Single(row => row.Id == "legacy-worker").RetentionStartedAtUtc);
        Assert.Equal(ended, rows.Single(row => row.Id == "legacy-snapshot").RetentionStartedAtUtc);
        Assert.All(rows, row => Assert.Null(row.ExpiresAtUtc));
        Assert.DoesNotContain(await IndexesAsync(fixture), index => index["name"] == "worker_log_expiration");
    }

    /// <summary>重启可补发同次JSON保存的意图，旧意图不能回滚更新的共享版本或本地显示值。</summary>
    [MongoHttpFact]
    public async Task DurableIntentReplaysAfterRestartAndSupersededIntentUsesEffectiveDays()
    {
        await using var fixture = await FixtureAsync();
        var policy = fixture.App.Services.GetRequiredService<TaskLogRetentionPolicy>();
        var revision = await policy.RevisionAsync();
        var pending = new TaskLogRetentionChange { Days = 0, ExpectedRevision = revision };
        await fixture.App.Services.GetRequiredService<ISettingsStore>().UpdateAsync(value =>
        {
            value.TaskLogRetentionDays = 0;
            value.TaskLogRetentionPending = pending;
            return value;
        });
        await fixture.RestartAsync();
        await StopWorkersAsync(fixture);
        await fixture.App.Services.GetRequiredService<TaskLogRetentionSettingsService>().ReplayPendingAsync();
        var saved = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(0, saved.TaskLogRetentionDays);
        Assert.Null(saved.TaskLogRetentionPending);
        policy = fixture.App.Services.GetRequiredService<TaskLogRetentionPolicy>();
        Assert.Null(await policy.CutoffAsync());
        await policy.ApplyAsync(30);
        await fixture.App.Services.GetRequiredService<ISettingsStore>().UpdateAsync(value =>
        {
            value.TaskLogRetentionDays = 1;
            value.TaskLogRetentionPending = new() { Days = 1, ExpectedRevision = revision };
            return value;
        });
        await fixture.App.Services.GetRequiredService<TaskLogRetentionSettingsService>().ReplayPendingAsync();
        saved = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(30, saved.TaskLogRetentionDays);
        Assert.Null(saved.TaskLogRetentionPending);
    }

    /// <summary>共享待应用版本和进程中断后留下的错误实际TTL都可恢复。</summary>
    [MongoHttpFact]
    public async Task PendingVersionAndActualIndexDriftAreReconciled()
    {
        await using var fixture = await FixtureAsync();
        var policy = fixture.App.Services.GetRequiredService<TaskLogRetentionPolicy>();
        await policy.InitializeAsync();
        await fixture.Context.Collection<BsonDocument>("taskLogRetentionPolicies").UpdateOneAsync(new BsonDocument("_id", "task-logs"),
            new BsonDocument { { "$set", new BsonDocument("Days", 0) }, { "$inc", new BsonDocument("Revision", 1) } });
        using var recovered = new TaskLogRetentionPolicy(fixture.Context);
        await recovered.InitializeAsync();
        Assert.DoesNotContain(await IndexesAsync(fixture), index => index["name"] == "task_log_retention");
        await fixture.Context.Collection<BsonDocument>("transcodeWorkerLogBatches").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("RetentionStartedAtUtc", 1), new CreateIndexOptions { Name = "task_log_retention", ExpireAfter = TimeSpan.FromDays(7) }));
        using var later = new TaskLogRetentionPolicy(fixture.Context);
        await later.InitializeAsync();
        Assert.DoesNotContain(await IndexesAsync(fixture), index => index["name"] == "task_log_retention");
    }

    /// <summary>延长期限不重新补建先前已经过期的历史录像日志。</summary>
    [MongoHttpFact]
    public async Task ExtendingRetentionDoesNotReimportExpiredRecordingSnapshots()
    {
        await using var fixture = await FixtureAsync();
        var ended = DateTime.UtcNow.AddDays(-10);
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = "expired-recording", State = "Completed", StartedAtUtc = ended.AddHours(-1), EndedAtUtc = ended, ErrorCode = "RECORDING_STREAM_TIMEOUT"
        });
        const string url = "/api/admin/worker-tasks/recording/expired-recording/logs?stage=Capturing";
        Assert.Empty((await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>(url))!.Entries);
        await fixture.App.Services.GetRequiredService<TaskLogRetentionPolicy>().ApplyAsync(0);
        Assert.Empty((await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>(url))!.Entries);
    }

    private static WorkerLogBatch Batch(string message, long sequence) => new()
    {
        StreamId = Guid.NewGuid().ToString("N"), BatchId = Guid.NewGuid().ToString("N"),
        Entries = [new() { Sequence = sequence, TimestampUtc = DateTime.UtcNow, JobId = "job", Attempt = 1, Stage = "Encoding", Message = message }]
    };

    private static DateTime Milliseconds(DateTime value) => new(value.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static async Task<List<BsonDocument>> IndexesAsync(HttpServerFixture fixture)
    {
        using var cursor = await fixture.Context.Collection<BsonDocument>("transcodeWorkerLogBatches").Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static async Task DropNewTtlAsync(HttpServerFixture fixture)
    {
        try
        {
            await fixture.Context.Collection<BsonDocument>("transcodeWorkerLogBatches").Indexes.DropOneAsync("task_log_retention");
        }
        catch (MongoCommandException error) when (error.Code is 26 or 27)
        {
        }
    }

    private static async Task<HttpServerFixture> FixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await StopWorkersAsync(fixture);
        return fixture;
    }

    private static async Task StopWorkersAsync(HttpServerFixture fixture)
    {
        foreach (var service in fixture.App.Services.GetServices<IHostedService>().Where(service => service is MediaJobWorker or RecordingSupervisor or TaskLogRetentionWorker))
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
