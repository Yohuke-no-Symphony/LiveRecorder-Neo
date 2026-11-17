using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证任务阶段日志先关联过滤再分页，并保持个人视图和技术日志权限。</summary>
public sealed class TaskLogHttpTests
{
    /// <summary>过滤跨越大量无关批次，不能把整个节点日志或旧执行代次泄露到阶段详情。</summary>
    [MongoHttpFact]
    public async Task StageFilteringPrecedesSparseLogPagination()
    {
        await using var fixture = await FixtureAsync();
        var owner = await fixture.CreateUserAsync("task-log-owner@example.test");
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(Job(owner.Id));
        var logs = fixture.App.Services.GetRequiredService<TranscodeWorkerLogStore>();
        for (var batch = 0; batch < 6; batch++)
        {
            var entries = Enumerable.Range(1, 64).Select(sequence => Entry(sequence, "unrelated", 2, "Encoding", "Other task")).ToList();
            if (batch == 0)
            {
                entries[0] = Entry(1, "job", 2, "Encoding", "First encoding line");
                entries[1] = Entry(2, "job", 1, "Encoding", "Previous attempt");
                entries[2] = Entry(3, "job", 2, "Uploading", "Different stage");
            }
            if (batch == 5)
            {
                entries[63] = Entry(64, "job", 2, "Encoding", "Second encoding line");
            }
            await logs.AppendAsync("worker", new() { StreamId = Guid.NewGuid().ToString("N"), BatchId = Guid.NewGuid().ToString("N"), Entries = entries });
        }
        var first = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/media/job/logs?stage=Encoding&after=0&take=1");
        Assert.Equal("First encoding line", Assert.Single(first!.Entries).Message);
        Assert.True(first.HasMore);
        var second = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>(
            "/api/admin/worker-tasks/media/job/logs?stage=Encoding&after=" + first.NextCursor + "&take=1");
        Assert.Equal("Second encoding line", Assert.Single(second!.Entries).Message);
        Assert.False(second.HasMore);
        Assert.True(second.NextCursor > first.NextCursor);
        var tail = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/media/job/logs?stage=Encoding&take=10");
        Assert.Equal(new[] { "First encoding line", "Second encoding line" }, tail!.Entries.Select(entry => entry.Message));
        Assert.All(tail.Entries, entry => Assert.Equal("Worker", entry.Source));
    }

    /// <summary>普通用户不能读取技术日志，管理员个人视图也不能越过本人任务范围。</summary>
    [MongoHttpFact]
    public async Task TechnicalLogsAreAdminOnlyAndRespectTaskVisibility()
    {
        await using var fixture = await FixtureAsync();
        var owner = await fixture.CreateUserAsync("task-log-permission@example.test");
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(Job(owner.Id));
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        using var personal = await browser.Client.GetAsync("/api/tasks/media/job/logs?stage=Encoding");
        Assert.Equal(HttpStatusCode.Forbidden, personal.StatusCode);
        using var administrative = await browser.Client.GetAsync("/api/admin/worker-tasks/media/job/logs?stage=Encoding");
        Assert.Equal(HttpStatusCode.Forbidden, administrative.StatusCode);
        using var adminPersonal = await fixture.Admin.Client.GetAsync("/api/tasks/media/job/logs?stage=Encoding");
        Assert.Equal(HttpStatusCode.NotFound, adminPersonal.StatusCode);
        using var missing = await fixture.Admin.Client.GetAsync("/api/admin/worker-tasks/media/missing/logs?stage=Encoding");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalidStage = await fixture.Admin.Client.GetAsync("/api/admin/worker-tasks/media/job/logs?stage=NotAStage");
        Assert.Equal(HttpStatusCode.BadRequest, invalidStage.StatusCode);
        using var invalidTake = await fixture.Admin.Client.GetAsync("/api/admin/worker-tasks/media/job/logs?stage=Encoding&take=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidTake.StatusCode);
        var coordinator = fixture.App.Services.GetRequiredService<TranscodingCoordinator>();
        await Assert.ThrowsAsync<InfrastructureException>(() => coordinator.AppendWorkerLogsAsync("worker", new()
        {
            StreamId = Guid.NewGuid().ToString("N"), BatchId = Guid.NewGuid().ToString("N"),
            Entries = [Entry(1, "job", 2, "Encoding", "Untrusted server event") with { Source = "ServerEvent" }]
        }));
    }

    /// <summary>删除节点身份不会删除任务日志，TTL到期条目则立即隐藏。</summary>
    [MongoHttpFact]
    public async Task ArchivedWorkerLogsRemainReadableUntilExpiration()
    {
        await using var fixture = await FixtureAsync();
        var owner = await fixture.CreateUserAsync("task-log-archive@example.test");
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(Job(owner.Id));
        var logs = fixture.App.Services.GetRequiredService<TranscodeWorkerLogStore>();
        await logs.AppendAsync("worker", new()
        {
            StreamId = Guid.NewGuid().ToString("N"), BatchId = Guid.NewGuid().ToString("N"),
            Entries = [Entry(1, "job", 2, "Encoding", "Retained after worker deletion")]
        });
        Assert.False(await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers").Find(worker => worker.Id == "worker").AnyAsync());
        var page = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/media/job/logs?stage=Encoding");
        Assert.Equal("Retained after worker deletion", Assert.Single(page!.Entries).Message);
        await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").UpdateManyAsync(batch => batch.WorkerId == "worker",
            Builders<TranscodeWorkerLogBatch>.Update.Set(batch => batch.RetentionStartedAtUtc, DateTime.UtcNow.AddDays(-8)));
        var expired = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/media/job/logs?stage=Encoding");
        Assert.Empty(expired!.Entries);
        Assert.Equal(page.NextCursor, expired.NextCursor);
    }

    /// <summary>旧任务的合成处理节点包含该代次日志，真实同名阶段仍保持精确过滤。</summary>
    [MongoHttpFact]
    public async Task LegacyProcessingStagesReadTheirAttemptWithoutBroadeningRealStages()
    {
        await using var fixture = await FixtureAsync();
        var owner = await fixture.CreateUserAsync("task-log-legacy@example.test");
        var job = Job(owner.Id);
        job.State = "Completed";
        job.FinishedAtUtc = DateTime.UtcNow;
        job.Stages = [];
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(job);
        await fixture.Context.Collection<TranscodeJob>("transcodeJobs").InsertOneAsync(new()
        {
            Id = "transcode", WorkerId = "worker", Attempt = 2, State = "Completed", Stage = "Completed", PublicationReady = true
        });
        var logs = fixture.App.Services.GetRequiredService<TranscodeWorkerLogStore>();
        await logs.AppendAsync("worker", new()
        {
            StreamId = Guid.NewGuid().ToString("N"), BatchId = Guid.NewGuid().ToString("N"),
            Entries =
            [
                Entry(1, "job", 1, "Encoding", "Previous attempt"),
                Entry(2, "job", 2, "Encoding", "Legacy media encoding"),
                Entry(3, "job", 2, "Processing", "Real processing line"),
                Entry(4, "transcode", 2, "Encoding", "Legacy transcode encoding")
            ]
        });
        var media = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/media/job/logs?stage=Processing&attempt=2");
        Assert.Equal(new[] { "Legacy media encoding", "Real processing line" }, media!.Entries.Select(entry => entry.Message));
        var transcode = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/transcode/transcode/logs?stage=Processing&attempt=2");
        Assert.Equal("Legacy transcode encoding", Assert.Single(transcode!.Entries).Message);
        await fixture.Context.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<MediaJob>.Update.Set(value => value.Stages, [new() { Id = "Processing", State = "Completed", Progress = 100 }]));
        var real = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/media/job/logs?stage=Processing&attempt=2");
        Assert.Equal("Real processing line", Assert.Single(real!.Entries).Message);
        using var stale = await fixture.Admin.Client.GetAsync("/api/admin/worker-tasks/media/job/logs?stage=Processing&attempt=1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    /// <summary>历史录像超时以信息性源事件呈现，重复查询不重复写入也不延长保留期。</summary>
    [MongoHttpFact]
    public async Task RecordingSnapshotIsInformationalIdempotentAndKeepsOriginalExpiration()
    {
        await using var fixture = await FixtureAsync();
        var ended = new DateTime(DateTime.UtcNow.AddHours(-1).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = "legacy-recording", State = "Completed", StartedAtUtc = ended.AddHours(-1), EndedAtUtc = ended,
            ErrorCode = "RECORDING_STREAM_TIMEOUT", ErrorArguments = ["20"]
        });
        const string url = "/api/admin/worker-tasks/recording/legacy-recording/logs?stage=Capturing&attempt=0";
        var page = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>(url);
        var entry = Assert.Single(page!.Entries);
        Assert.Equal("SessionSnapshot", entry.Source);
        Assert.Equal("Information", entry.Level);
        Assert.Equal(ended, entry.TimestampUtc);
        Assert.Contains("20", entry.Message);
        var repeated = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>(url);
        Assert.Equal(entry.Id, Assert.Single(repeated!.Entries).Id);
        Assert.Equal(page.NextCursor, repeated.NextCursor);
        var batch = await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Find(value => value.WorkerId == "local").SingleAsync();
        Assert.Equal(ended, batch.RetentionStartedAtUtc);
        Assert.Null(batch.ExpiresAtUtc);
        var uploading = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/recording/legacy-recording/logs?stage=Uploading");
        Assert.Empty(uploading!.Entries);
        await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(value => value.Id == "legacy-recording",
            Builders<BroadcastSession>.Update.Set(value => value.EndedAtUtc, ended.AddDays(-8)));
        await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").DeleteManyAsync(value => value.WorkerId == "local");
        var expired = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>(url);
        Assert.Empty(expired!.Entries);
        Assert.False(await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Find(value => value.WorkerId == "local").AnyAsync());
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = "active-recording", State = "Recording", StartedAtUtc = ended, LastMediaAtUtc = DateTime.UtcNow,
            ErrorCode = "RECORDING_STREAM_TIMEOUT", ErrorArguments = ["20"]
        });
        var active = await fixture.Admin.Client.GetFromJsonAsync<TranscodeWorkerLogPage>("/api/admin/worker-tasks/recording/active-recording/logs?stage=Capturing");
        Assert.Empty(active!.Entries);
        Assert.False(await fixture.Context.Collection<TranscodeWorkerLogBatch>("transcodeWorkerLogBatches").Find(value => value.WorkerId == "local").AnyAsync());
    }

    /// <summary>省略新增来源字段的旧批次保留原序列化形状及重发内容哈希。</summary>
    [Fact]
    public void LegacyLogEntriesDoNotSerializeAnAdditionalNullSource()
    {
        var json = JsonSerializer.Serialize(Entry(1, "job", 1, "Encoding", "Legacy"), WorkerProtocol.JsonOptions);
        Assert.DoesNotContain("source", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MediaJob Job(string ownerId)
    {
        var stages = WorkerStageSnapshotPolicy.Create("Clipping");
        stages.Single(stage => stage.Id == "Encoding").State = "Running";
        return new()
        {
            Id = "job", OwnerId = ownerId, Provenance = TaskProvenance.User(ownerId, "Owner"),
            State = "Running", Attempt = 2, RemoteWorkerId = "worker", Title = "Task logs", Stages = stages
        };
    }

    private static WorkerLogEntry Entry(long sequence, string jobId, int attempt, string stage, string message) => new()
    {
        Sequence = sequence, TimestampUtc = DateTime.UtcNow, JobId = jobId, Attempt = attempt, Stage = stage, Message = message
    };

    private static async Task<HttpServerFixture> FixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "false");
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        foreach (var service in fixture.App.Services.GetServices<IHostedService>().Where(service => service is MediaJobWorker or RecordingSupervisor))
        {
            await service.StopAsync(CancellationToken.None);
        }
        return fixture;
    }
}
