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
    /// <summary>等待节点的 AI 任务不伪装为主站执行，升级前已完成任务仍保留本地来源。</summary>
    [MongoHttpFact]
    public async Task UnassignedAiTasksWaitForNodesWhileLegacyHistoryRemainsLocal()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").InsertManyAsync(
        [
            new() { Id = "waiting-ai-node", State = "Queued", Input = new(), Snapshot = new() { Title = "waiting" } },
            new() { Id = "legacy-ai-local", State = "Completed", FinishedAtUtc = DateTime.UtcNow, Snapshot = new() { Title = "legacy" } }
        ]);

        var page = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks?kind=ai");

        Assert.NotNull(page);
        Assert.Null(Assert.Single(page.Items, value => value.Id == "waiting-ai-node").WorkerId);
        Assert.Equal("local", Assert.Single(page.Items, value => value.Id == "legacy-ai-local").WorkerId);
    }

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
        Assert.Equal("Waiting", recording.Stages[1].State);
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
        Assert.Equal("Running", before.Stages[0].State);
        Assert.Equal("Waiting", before.Stages[1].State);
        Assert.Equal("Queued", before.Stages[1].Substate);
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

    /// <summary>中断只收尾历史，原有归档和其它采集保留，找回缓存后能重新上报。</summary>
    [MongoHttpFact]
    public async Task InterruptedRecordingLeavesActiveListAndCanResumeAfterDismissal()
    {
        await using var fixture = await FixtureAsync();
        var workers = fixture.App.Services.GetRequiredService<TranscodeWorkerService>();
        var worker = (await workers.CreateAsync("windows1", "Recording", CancellationToken.None)).Worker;
        var ownership = fixture.App.Services.GetRequiredService<RecordingOwnershipService>();
        var assignment = (await ownership.TryAcquireWorkerAsync(new() { RoomId = 95701 }, worker.Id, CancellationToken.None))!;
        var old = StaleRecording("old-recording", assignment);
        var current = StaleRecording("current-recording", assignment);
        var snapshots = fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks");
        await snapshots.InsertManyAsync([old, current]);
        var original = await snapshots.Find(value => value.Id == old.Id).SingleAsync();
        await fixture.Context.Collection<RecordingAssignmentRecord>("recordingAssignments").UpdateOneAsync(value => value.Id == assignment.Id,
            Builders<RecordingAssignmentRecord>.Update.Set(value => value.CurrentSessionId, current.Id));
        var receipts = fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions");
        await receipts.InsertOneAsync(new() { Id = old.Id, AssignmentId = assignment.Id, Generation = assignment.Generation, Descriptor = old.Descriptor, Published = true });
        var sessions = fixture.Context.Collection<BroadcastSession>("sessions");
        await sessions.InsertOneAsync(new() { Id = old.Id, StartedAtUtc = old.Descriptor.StartedAtUtc, RecordingAssignmentId = assignment.Id, TotalBytes = 100 });
        var path = "/api/admin/worker-tasks/recording/" + old.Id;
        var before = (await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>(path))!;
        Assert.True(before.CanInterruptRecording);
        Assert.True(before.CanViewResults);

        using var interrupted = await fixture.Admin.SendAsync(HttpMethod.Post, path + "/interrupt", new RecordingTaskInterruptRequest(before.LastUpdatedAtUtc!.Value));

        Assert.Equal(HttpStatusCode.NoContent, interrupted.StatusCode);
        var active = (await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks?kind=recording&state=active"))!;
        Assert.Equal(current.Id, Assert.Single(active.Items).Id);
        Assert.Equal(1, active.Total);
        using var preview = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/admin/worker-tasks/active", new TaskHistoryActiveRequest([worker.Id]));
        var perWorker = (await preview.Content.ReadFromJsonAsync<Dictionary<string, BackgroundTaskSummary[]>>())!;
        Assert.Equal(current.Id, Assert.Single(perWorker[worker.Id]).Id);
        var unfinished = (await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks?kind=recording&state=Interrupted"))!;
        var row = Assert.Single(unfinished.Items);
        var stored = await snapshots.Find(value => value.Id == old.Id).SingleAsync();
        var interruptions = fixture.Context.Collection<RecordingTaskInterruption>("recordingTaskInterruptions");
        var disposition = await interruptions.Find(value => value.Id == old.Id).SingleAsync();
        Assert.Equal(old.Id, row.Id);
        Assert.Equal("Interrupted", row.State);
        Assert.Equal(disposition.CreatedAtUtc, row.FinishedAtUtc);
        Assert.Equal(original.LastReportedAtUtc, disposition.LastReportedAtUtc);
        Assert.False(string.IsNullOrEmpty(disposition.InterruptedByUserId));
        Assert.Equal(original.Descriptor, stored.Descriptor);
        Assert.Equal(old.CommittedArchiveBytes, stored.CommittedArchiveBytes);
        Assert.Equal(old.AcknowledgedArchiveBytes, stored.AcknowledgedArchiveBytes);
        Assert.True(row.CanViewResults);
        Assert.True(row.CanDelete);
        Assert.False(row.CanInterruptRecording);
        Assert.All(row.Stages, stage =>
        {
            Assert.Equal("Interrupted", stage.State);
            Assert.Null(stage.Substate);
        });
        Assert.NotNull(row.Stages[1].Error);
        using var repeated = await fixture.Admin.SendAsync(HttpMethod.Post, path + "/interrupt", new RecordingTaskInterruptRequest(before.LastUpdatedAtUtc.Value));
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);

        using var dismissed = await fixture.Admin.SendAsync(HttpMethod.Delete, path, new TaskHistoryDeleteRequest(row.Attempt, row.FinishedAtUtc!.Value, row.State));
        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        using var hidden = await fixture.Admin.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.True((await receipts.Find(value => value.Id == old.Id).SingleAsync()).Published);
        Assert.Equal(100, (await sessions.Find(value => value.Id == old.Id).SingleAsync()).TotalBytes);
        Assert.Equal(current.Id, (await fixture.Context.Collection<RecordingAssignmentRecord>("recordingAssignments")
            .Find(value => value.Id == assignment.Id).SingleAsync()).CurrentSessionId);

        await fixture.App.Services.GetRequiredService<RecordingSessionTaskService>().ReportAsync(worker.Id, [new()
        {
            AssignmentId = assignment.Id, Generation = assignment.Generation, Token = ownership.Token(assignment),
            Descriptor = old.Descriptor, CaptureState = "Paused", UploadState = "Queued",
            CommittedArchiveBytes = old.CommittedArchiveBytes, AcknowledgedArchiveBytes = old.AcknowledgedArchiveBytes
        }], CancellationToken.None);
        var resumed = (await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>(path))!;
        Assert.Equal("Running", resumed.State);
        Assert.False(resumed.CanInterruptRecording);
        Assert.Equal(disposition.CreatedAtUtc, (await interruptions.Find(value => value.Id == old.Id).SingleAsync()).CreatedAtUtc);
    }

    /// <summary>新进度、当前采集和非管理员请求不能把场次结束，已释放的旧分配可以处理。</summary>
    [MongoHttpFact]
    public async Task RecordingInterruptionRejectsStaleConfirmationAndCurrentAssignment()
    {
        await using var fixture = await FixtureAsync();
        var viewer = await fixture.CreateUserAsync("recording-interruption-viewer@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);
        var assignment = new RecordingAssignmentRecord { OwnerId = "windows1", Generation = 2, RoomId = 95702, CurrentSessionId = "old-recording" };
        var assignments = fixture.Context.Collection<RecordingAssignmentRecord>("recordingAssignments");
        await assignments.InsertOneAsync(assignment);
        var old = StaleRecording("old-recording", assignment);
        var snapshots = fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks");
        await snapshots.InsertOneAsync(old);
        var path = "/api/admin/worker-tasks/recording/" + old.Id;
        var body = new RecordingTaskInterruptRequest(old.LastReportedAtUtc);
        using var forbidden = await browser.SendAsync(HttpMethod.Post, path + "/interrupt", body);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.False((await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>(path))!.CanInterruptRecording);
        using var current = await fixture.Admin.SendAsync(HttpMethod.Post, path + "/interrupt", body);
        Assert.Equal(HttpStatusCode.Conflict, current.StatusCode);
        await assignments.UpdateOneAsync(value => value.Id == assignment.Id, Builders<RecordingAssignmentRecord>.Update.Set(value => value.State, "Released"));
        var before = (await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>(path))!;
        Assert.True(before.CanInterruptRecording);
        var reported = old.LastReportedAtUtc.AddMinutes(1);
        await snapshots.UpdateOneAsync(value => value.Id == old.Id, Builders<RecordingSessionTaskSnapshot>.Update.Set(value => value.LastReportedAtUtc, reported));
        using var stale = await fixture.Admin.SendAsync(HttpMethod.Post, path + "/interrupt", body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.False(await fixture.Context.Collection<RecordingTaskInterruption>("recordingTaskInterruptions").Find(value => value.Id == old.Id).AnyAsync());
        using var applied = await fixture.Admin.SendAsync(HttpMethod.Post, path + "/interrupt", new RecordingTaskInterruptRequest(reported));
        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
    }

    /// <summary>隐藏中断记录后，真实归档完成仍按独立终态展示。</summary>
    [MongoHttpFact]
    public async Task DismissedRecordingInterruptionDoesNotHideCompletedArchive()
    {
        await using var fixture = await FixtureAsync();
        var old = StaleRecording("sealed-after-interruption", new() { OwnerId = "windows1", Generation = 2, RoomId = 95703 });
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").InsertOneAsync(old);
        var path = "/api/admin/worker-tasks/recording/" + old.Id;
        using var interrupted = await fixture.Admin.SendAsync(HttpMethod.Post, path + "/interrupt", new RecordingTaskInterruptRequest(old.LastReportedAtUtc));
        Assert.Equal(HttpStatusCode.NoContent, interrupted.StatusCode);
        var disposition = await fixture.Context.Collection<RecordingTaskInterruption>("recordingTaskInterruptions").Find(value => value.Id == old.Id).SingleAsync();
        var request = new TaskHistoryDeleteRequest(0, disposition.CreatedAtUtc, "Interrupted");
        using var dismissed = await fixture.Admin.SendAsync(HttpMethod.Delete, path, request);
        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions").InsertOneAsync(new()
        {
            Id = old.Id, Descriptor = old.Descriptor, Published = true, FinalizedAtUtc = disposition.CreatedAtUtc
        });
        var completed = (await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>(path))!;
        Assert.Equal("Completed", completed.State);
        Assert.False(completed.CanInterruptRecording);
        Assert.Null(completed.Error);
        Assert.All(completed.Stages, stage => Assert.Equal("Completed", stage.State));
        using var stale = await fixture.Admin.SendAsync(HttpMethod.Delete, path, request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var legacy = await fixture.Admin.SendAsync(HttpMethod.Delete, path, request with { State = null });
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
    }

    /// <summary>超时结束的成功录像和已归档的重试快照不再携带当前错误。</summary>
    [MongoHttpFact]
    public async Task CompletedRecordingsDoNotExposeHistoricalTimeoutsAsCurrentFailures()
    {
        await using var fixture = await FixtureAsync();
        var ended = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = "timeout-completed", State = "Completed", StartedAtUtc = ended.AddHours(-1), EndedAtUtc = ended,
            ErrorCode = "RECORDING_STREAM_TIMEOUT", ErrorArguments = ["5"]
        });
        var descriptor = new RecordingSessionDescriptor { SessionId = "retry-completed", StartedAtUtc = ended.AddHours(-1), EndedAtUtc = ended, IsClosed = true };
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, Descriptor = descriptor, CaptureState = "Recording", UploadState = "Retrying", ErrorCode = "RecordingUploadFailed"
        });
        await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, Descriptor = descriptor, FinalizedAtUtc = ended
        });
        var page = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks");
        Assert.Equal(2, page!.Items.Count);
        Assert.All(page.Items, task =>
        {
            Assert.Equal("Completed", task.State);
            Assert.Null(task.Error);
            Assert.All(task.Stages, stage =>
            {
                Assert.Equal("Completed", stage.State);
                Assert.Equal(100, stage.Progress);
                Assert.Null(stage.Error);
            });
            Assert.Equal("Closed", task.Stages[0].Substate);
        });
    }

    /// <summary>录像上传重试不污染采集阶段，采集重连不污染上传阶段。</summary>
    [MongoHttpFact]
    public async Task RecordingRetryDiagnosticsBelongToTheirOwnStage()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Context.Collection<RecordingSessionTaskSnapshot>("recordingSessionTasks").InsertManyAsync([
            new()
            {
                Id = "upload-retry", Descriptor = new() { SessionId = "upload-retry", StartedAtUtc = DateTime.UtcNow },
                CaptureState = "Recording", UploadState = "Retrying", ErrorCode = "RecordingUploadFailed"
            },
            new()
            {
                Id = "capture-retry", Descriptor = new() { SessionId = "capture-retry", StartedAtUtc = DateTime.UtcNow },
                CaptureState = "Retrying", UploadState = "CaughtUp", ErrorCode = "RecordingSourceUnavailable"
            },
            new()
            {
                Id = "healthy", Descriptor = new() { SessionId = "healthy", StartedAtUtc = DateTime.UtcNow },
                CaptureState = "Recording", UploadState = "CaughtUp", ErrorCode = "RecordingSourceUnavailable"
            }
        ]);
        var page = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks");
        var upload = Assert.Single(page!.Items, task => task.Id == "upload-retry");
        Assert.Null(upload.Stages[0].Error);
        Assert.NotNull(upload.Stages[1].Error);
        var capture = Assert.Single(page.Items, task => task.Id == "capture-retry");
        Assert.NotNull(capture.Stages[0].Error);
        Assert.Null(capture.Stages[1].Error);
        var healthy = Assert.Single(page.Items, task => task.Id == "healthy");
        Assert.Null(healthy.Error);
        Assert.All(healthy.Stages, stage => Assert.Null(stage.Error));
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

    /// <summary>阶段详情保留业务错误参数，并兼容参数缺失的旧记录。</summary>
    [MongoHttpFact]
    public async Task MediaErrorDiagnosticsAreLocalizedWithArguments()
    {
        await using var fixture = await FixtureAsync();
        var formatted = Media("formatted-error", "owner");
        formatted.State = "Failed";
        formatted.Error = "old error text";
        formatted.ErrorCode = "JOB_PROCESSING_FAILED";
        formatted.ErrorArguments = ["specific diagnostic"];
        var legacy = Media("legacy-error", "owner");
        legacy.State = "Failed";
        legacy.ErrorCode = "JOB_PROCESSING_FAILED";
        await fixture.Context.Collection<MediaJob>("jobs").InsertManyAsync([formatted, legacy]);
        fixture.Admin.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        var page = await fixture.Admin.Client.GetFromJsonAsync<TaskHistoryPage>("/api/admin/worker-tasks");
        var row = Assert.Single(page!.Items, value => value.Id == formatted.Id);
        Assert.Contains("specific diagnostic", row.Error);
        Assert.DoesNotContain("old error text", row.Error);
        Assert.Equal(row.Error, Assert.Single(row.Stages).Error);
        Assert.Equal(2, page.Items.Count);
    }

    /// <summary>个人详情保留可理解的失败状态，原始工具路径仅向管理员开放。</summary>
    [MongoHttpFact]
    public async Task PersonalStageDetailsDoNotExposePrivateToolPaths()
    {
        await using var fixture = await FixtureAsync();
        var owner = await fixture.CreateUserAsync("task-diagnostics@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        var job = Media("private-diagnostics", owner.Id);
        job.State = "Failed";
        job.Error = "FFmpeg failed opening /Users/internal/private.flv";
        job.ErrorCode = "WorkerExecutionFailed";
        job.Stages = [new()
        {
            Id = "Encoding", State = "Failed", Error = job.Error,
            Activity = new() { CurrentFile = "C:\\private\\secret.flv" }
        }];
        await fixture.Context.Collection<MediaJob>("jobs").InsertOneAsync(job);
        using var response = await browser.Client.GetAsync("/api/tasks/media/private-diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("/Users/internal", text);
        Assert.DoesNotContain("private\\\\secret.flv", text);
        var task = (await response.Content.ReadFromJsonAsync<BackgroundTaskSummary>())!;
        Assert.NotEmpty(task.Error!);
        Assert.Equal("secret.flv", Assert.Single(Assert.Single(task.Stages).Metrics).Text);
        var administrator = await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>("/api/admin/worker-tasks/media/private-diagnostics");
        Assert.Contains("/Users/internal", administrator!.Error);
    }

    /// <summary>旧节点的阶段指标与新节点尚无确定进度的步骤采用真实数据投影。</summary>
    [MongoHttpFact]
    public async Task LegacyProgressDoesNotInventCompletedStagesOrUseOverallPercentAsStagePercent()
    {
        await using var fixture = await FixtureAsync();
        var legacy = Transcode("legacy-progress", "worker", TaskProvenance.Automatic());
        legacy.State = "Running";
        legacy.Stage = "Encoding";
        legacy.Progress = 50;
        legacy.FinishedAtUtc = null;
        legacy.Activity = new() { ProcessedDurationMs = 25, TotalDurationMs = 100 };
        legacy.Stages = WorkerStageSnapshotPolicy.Create("Transcoding");
        var inspecting = Transcode("inspecting", "worker", TaskProvenance.Automatic());
        inspecting.State = "Running";
        inspecting.FinishedAtUtc = null;
        inspecting.Stages = [new() { Id = "Inspecting", State = "Running" }];
        await fixture.Context.Collection<TranscodeJob>("transcodeJobs").InsertManyAsync([legacy, inspecting]);
        var old = await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>("/api/admin/worker-tasks/transcode/legacy-progress");
        var stage = Assert.Single(old!.Stages);
        Assert.Equal("Encoding", stage.Id);
        Assert.Equal(25, stage.Progress);
        Assert.Null(stage.ElapsedMs);
        var current = await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>("/api/admin/worker-tasks/transcode/inspecting");
        Assert.Null(Assert.Single(current!.Stages).Progress);
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

    private static RecordingSessionTaskSnapshot StaleRecording(string id, RecordingAssignmentRecord assignment) => new()
    {
        Id = id, WorkerId = assignment.OwnerId, AssignmentId = assignment.Id, Generation = assignment.Generation,
        Descriptor = new() { SessionId = id, RoomId = assignment.RoomId, StartedAtUtc = DateTime.UtcNow.AddHours(-2) },
        CaptureState = "Paused", UploadState = "Queued", LastReportedAtUtc = DateTime.UtcNow.AddHours(-1),
        CommittedArchiveBytes = 150, AcknowledgedArchiveBytes = 100
    };

    private static async Task<HttpServerFixture> FixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            foreach (var worker in fixture.App.Services.GetServices<IHostedService>().Where(value =>
                value is MediaJobWorker or AiJobMaintenanceWorker or RecordingSupervisor or TranscodeMaintenanceService))
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
