using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证两种任务视图的权限、聚合分页以及独立历史清理。</summary>
public sealed class TaskHistoryHttpTests
{
    /// <summary>个人页包含本人手动来源，管理员页包含全部来源且不暴露私有字段。</summary>
    [MongoHttpFact]
    public async Task PersonalScopeIsOwnedEvenForAdministratorsAndAdminScopeIncludesEverySource()
    {
        await using var fixture = await FixtureAsync();
        var owner = await fixture.CreateUserAsync("task-owner@example.test");
        var other = await fixture.CreateUserAsync("task-other@example.test");
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(value => value.Id == owner.Id,
            Builders<UserAccount>.Update.Set(value => value.Permissions, new PermissionSet([PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS, PermissionIds.GENERATE_ANALYTICS])));
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await fixture.Context.Collection<MediaJob>("jobs").InsertManyAsync([
            Media("mine", owner.Id), Media("other", other.Id)
        ]);
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").InsertOneAsync(new()
        {
            Id = "analysis", OwnerId = owner.Id, State = "Completed", FinishedAtUtc = DateTime.UtcNow,
            Snapshot = new() { Title = "我的分析", EvidenceRelativePath = "private-evidence" }, Settings = new() { ApiKey = "private-api-key" }
        });
        await fixture.Context.Collection<TranscodeJob>("transcodeJobs").InsertManyAsync([
            Transcode("automatic", "worker-a", TaskProvenance.Automatic()),
            Transcode("manual", "worker-a", TaskProvenance.User(owner.Id, owner.DisplayName)),
            Transcode("legacy", "worker-a", new())
        ]);
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").InsertOneAsync(new()
        {
            Id = "recording", WorkerId = "worker-a", Descriptor = new() { SessionId = "recording", Title = "自动录像", StartedAtUtc = DateTime.UtcNow },
            CaptureState = "Recording", UploadState = "CaughtUp", CommittedArchiveBytes = 100, AcknowledgedArchiveBytes = 100
        });
        var mine = await browser.Client.GetFromJsonAsync<TaskHistoryPage>("/api/tasks");
        Assert.Equal(["analysis", "mine"], mine!.Items.Select(value => value.Id).Order().ToArray());
        using var forbidden = await browser.Client.GetAsync("/api/admin/worker-tasks");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var ownAdmin = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/tasks");
        Assert.Empty(ownAdmin!.Items);
        using var response = await fixture.Admin.Client.GetAsync("/api/admin/worker-tasks");
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-", text);
        Assert.DoesNotContain("leaseToken", text, StringComparison.OrdinalIgnoreCase);
        var all = await response.Content.ReadFromJsonAsync<TaskHistoryPage>();
        Assert.Equal(7, all!.Total);
        Assert.Equal("Legacy", Assert.Single(all.Items, value => value.Id == "legacy").Origin);
        var recording = Assert.Single(all.Items, value => value.Id == "recording");
        Assert.Equal("Running", recording.State);
        Assert.Null(recording.Stages[0].Progress);
        Assert.Equal("Running", recording.Stages[1].State);
        Assert.Equal("CaughtUp", recording.Stages[1].Substate);
        Assert.Equal(100, recording.Stages[1].Progress);
    }

    /// <summary>删除只隐藏当前视图中的终态，实体保留，重试后重新出现。</summary>
    [MongoHttpFact]
    public async Task DismissalPreservesBusinessRecordsAndDoesNotHideAnotherScopeOrRetry()
    {
        await using var fixture = await FixtureAsync();
        var owner = await fixture.CreateUserAsync("task-dismiss@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        var job = Media("completed", owner.Id);
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(job);
        using var deleted = await browser.SendAsync(HttpMethod.Delete, "/api/tasks/media/completed", new TaskHistoryDeleteRequest(job.Attempt, job.FinishedAtUtc!.Value));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await browser.Client.GetFromJsonAsync<TaskHistoryPage>("/api/tasks"))!.Items);
        Assert.Single((await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks"))!.Items);
        Assert.True(await fixture.Context.Collection<MediaJob>("jobs").Find(value => value.Id == job.Id).AnyAsync());
        await fixture.Context.Collection<MediaJob>("jobs").UpdateOneAsync(value => value.Id == job.Id,
            Builders<MediaJob>.Update.Set(value => value.State, "Queued").Set(value => value.FinishedAtUtc, null));
        var retry = Assert.Single((await browser.Client.GetFromJsonAsync<TaskHistoryPage>("/api/tasks"))!.Items);
        Assert.Equal("Queued", retry.State);
        Assert.False(retry.CanDelete);
        using var stale = await browser.SendAsync(HttpMethod.Delete, "/api/tasks/media/completed", new TaskHistoryDeleteRequest(job.Attempt, job.FinishedAtUtc.Value));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    /// <summary>清空覆盖全部分页，遵守筛选和确认截止时间，保留失败及其他节点任务。</summary>
    [MongoHttpFact]
    public async Task ClearCompletedHonorsFilterCutoffAndAllPages()
    {
        await using var fixture = await FixtureAsync();
        var jobs = Enumerable.Range(0, 507).Select(index => Transcode("bulk-" + index, "worker-a", TaskProvenance.Automatic())).ToList();
        jobs.Add(Transcode("other-worker", "worker-b", TaskProvenance.Automatic()));
        var failed = Transcode("failed", "worker-a", TaskProvenance.Automatic());
        failed.State = "Failed";
        jobs.Add(failed);
        await fixture.Context.Collection<TranscodeJob>("transcodeJobs").InsertManyAsync(jobs);
        var query = new TaskHistoryQuery { WorkerId = "worker-a" };
        var preview = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryClearPreview>("/api/admin/worker-tasks/clear-completed/preview?workerId=worker-a");
        Assert.Equal(507, preview!.Count);
        var page = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks?workerId=worker-a&limit=3");
        Assert.Equal(508, page!.Total);
        Assert.Equal(3, page.Items.Count);
        Assert.NotNull(page.NextCursor);
        var second = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks?workerId=worker-a&limit=3&cursor=" + Uri.EscapeDataString(page.NextCursor!));
        Assert.Empty(page.Items.Select(value => value.Id).Intersect(second!.Items.Select(value => value.Id)));
        var fresh = Transcode("fresh", "worker-a", TaskProvenance.Automatic());
        fresh.FinishedAtUtc = preview.CompletedBeforeUtc.AddSeconds(1);
        await fixture.Context.Collection<TranscodeJob>("transcodeJobs").InsertOneAsync(fresh);
        using var cleared = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/admin/worker-tasks/clear-completed", new TaskHistoryClearRequest(query, preview.CompletedBeforeUtc));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var remaining = (await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks"))!.Items.Select(value => value.Id).Order().ToArray();
        Assert.Equal(["failed", "fresh", "other-worker"], remaining);
        Assert.Equal(510, await fixture.Context.Collection<TranscodeJob>("transcodeJobs").CountDocumentsAsync(FilterDefinition<TranscodeJob>.Empty));
    }

    /// <summary>数据库完成状态尚未激活发布时不能作为成功记录清理。</summary>
    [MongoHttpFact]
    public async Task PendingPublicationRemainsActiveAndCannotBeDeleted()
    {
        await using var fixture = await FixtureAsync();
        var transcode = Transcode("publishing", "worker", TaskProvenance.Automatic());
        transcode.PublicationReady = false;
        await fixture.Context.Collection<TranscodeJob>("transcodeJobs").InsertOneAsync(transcode);
        var media = Media("clip-publishing", "owner");
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(media);
        await fixture.Context.Collection<Clip>("clips").InsertOneAsync(new() { JobId = media.Id, PublicationAttempt = media.Attempt, PublicationPending = true });
        var all = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks");
        Assert.All(all!.Items, task =>
        {
            Assert.Equal("Publishing", task.State);
            Assert.False(task.CanDelete);
            Assert.False(task.CanCancel);
        });
        var preview = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryClearPreview>("/api/admin/worker-tasks/clear-completed/preview");
        Assert.Equal(0, preview!.Count);
        using var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/admin/worker-tasks/transcode/publishing", new TaskHistoryDeleteRequest(0, transcode.FinishedAtUtc!.Value));
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
    }

    /// <summary>首段前可见录像，补传进入录像库后同一任务键和排序时间保持不变。</summary>
    [MongoHttpFact]
    public async Task RecordingSourcesMergeWithoutDuplicatingOrMovingSession()
    {
        await using var fixture = await FixtureAsync();
        var started = DateTime.UtcNow.AddMinutes(-10);
        var descriptor = new RecordingSessionDescriptor { SessionId = "session", StartedAtUtc = started, Title = "录制中" };
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, Descriptor = descriptor, WorkerId = "worker", CaptureState = "Recording", UploadState = "Queued"
        });
        var before = Assert.Single((await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks"))!.Items);
        Assert.False(before.CanViewResults);
        await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, Descriptor = descriptor, Published = true
        });
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, RecordingAssignmentId = "assignment", StartedAtUtc = started, Title = descriptor.Title
        });
        var after = Assert.Single((await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks"))!.Items);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
        Assert.True(after.CanViewResults);
        Assert.Equal("Running", after.State);
    }

    /// <summary>新清单的显式未完成标记不能被库中的完成状态覆盖。</summary>
    [MongoHttpFact]
    public async Task RecordingCompletionWaitsForFinalizationAndCannotBeRegressedByHeartbeat()
    {
        await using var fixture = await FixtureAsync();
        var ended = DateTime.UtcNow.AddSeconds(-10);
        var descriptor = new RecordingSessionDescriptor { SessionId = "finalizing", StartedAtUtc = ended.AddMinutes(-1), EndedAtUtc = ended, IsClosed = true };
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, WorkerId = "worker", Descriptor = descriptor, CaptureState = "Closed", UploadState = "Finalizing"
        });
        await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions").InsertOneAsync(new() { Id = descriptor.SessionId, Descriptor = descriptor });
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, RecordingAssignmentId = "assignment", State = "Completed", StartedAtUtc = descriptor.StartedAtUtc, EndedAtUtc = ended
        });
        var pending = Assert.Single((await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks"))!.Items);
        Assert.Equal("Running", pending.State);
        Assert.False(pending.CanDelete);
        await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions").UpdateOneAsync(value => value.Id == descriptor.SessionId,
            Builders<RecordingSessionReceipt>.Update.Set(value => value.FinalizedAtUtc, DateTime.UtcNow));
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").UpdateOneAsync(value => value.Id == descriptor.SessionId,
            Builders<RecordingSessionTaskSnapshot>.Update.Set(value => value.CaptureState, "Recording").Set(value => value.UploadState, "Uploading"));
        var completed = Assert.Single((await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks"))!.Items);
        Assert.Equal("Completed", completed.State);
        Assert.True(completed.CanDelete);
        Assert.All(completed.Stages, stage => Assert.Equal("Completed", stage.State));
    }

    /// <summary>节点卡片批量预览独立限制每个节点，已取消任务不会残留活动动画。</summary>
    [MongoHttpFact]
    public async Task ActivePreviewLimitsEachWorkerAndTerminalStagesStopRunning()
    {
        await using var fixture = await FixtureAsync();
        var jobs = Enumerable.Range(0, 8).Select(index => Transcode("active-" + index, index < 6 ? "worker-a" : "worker-b", TaskProvenance.Automatic())).ToArray();
        foreach (var job in jobs)
        {
            job.State = "Running";
            job.FinishedAtUtc = null;
        }
        var cancelled = Transcode("cancelled", "worker-a", TaskProvenance.Automatic());
        cancelled.State = "Cancelled";
        cancelled.Stages = [new() { Id = "Downloading", State = "Completed", Progress = 100 }, new() { Id = "Encoding", State = "Running", Progress = 20 }];
        await fixture.Context.Collection<TranscodeJob>("transcodeJobs").InsertManyAsync([.. jobs, cancelled]);
        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/admin/worker-tasks/active", new TaskHistoryActiveRequest(["worker-a", "worker-b"]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var active = (await response.Content.ReadFromJsonAsync<Dictionary<string, BackgroundTaskSummary[]>>())!;
        Assert.Equal(3, active["worker-a"].Length);
        Assert.Equal(2, active["worker-b"].Length);
        Assert.DoesNotContain(active.Values.SelectMany(value => value), task => task.Id == cancelled.Id);
        var row = await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>("/api/admin/worker-tasks/transcode/cancelled");
        Assert.Equal("Completed", row!.Stages[0].State);
        Assert.Equal("Cancelled", row.Stages[1].State);
    }

    /// <summary>不完整游标和无效清理请求返回业务校验错误。</summary>
    [MongoHttpFact]
    public async Task InvalidCursorAndClearFilterAreRejected()
    {
        await using var fixture = await FixtureAsync();
        var malformed = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"Kind\":\"media\",\"Id\":null}"));
        using var cursor = await fixture.Admin.Client.GetAsync("/api/admin/worker-tasks?cursor=" + Uri.EscapeDataString(malformed));
        Assert.Equal(HttpStatusCode.BadRequest, cursor.StatusCode);
        using var clear = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/admin/worker-tasks/clear-completed", new { filter = (TaskHistoryQuery?)null, completedBeforeUtc = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.BadRequest, clear.StatusCode);
    }

    private static MediaJob Media(string id, string owner) => new()
    {
        Id = id, OwnerId = owner, Title = id, Kind = "Clip", State = "Completed", FinishedAtUtc = DateTime.UtcNow.AddMinutes(-1)
    };

    private static TranscodeJob Transcode(string id, string worker, TaskProvenance provenance) => new()
    {
        Id = id, Title = id, WorkerId = worker, Provenance = provenance, State = "Completed", PublicationReady = true,
        FinishedAtUtc = DateTime.UtcNow.AddMinutes(-1)
    };

    private static async Task<HttpServerFixture> FixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            foreach (var worker in fixture.App.Services.GetServices<IHostedService>().Where(value =>
                value is MediaJobWorker or AiAnalysisWorker or RecordingSupervisor or TranscodeMaintenanceService))
            {
                await worker.StopAsync(CancellationToken.None);
            }
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }
}
