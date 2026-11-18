using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过真实 HTTP 验证 AI 共享报告、独立操作权限和管理员密钥契约，不调用外部模型。</summary>
public sealed class AiAnalysisHttpTests
{
    /// <summary>报告读取与模型操作分别授权，生成权限也不能访问管理员密钥设置。</summary>
    [MongoHttpFact]
    public async Task SharedReportsAreReadableWithoutGenerationAndAdministrationPermissions()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var session = await SeedSessionAsync(fixture, true);
        var completed = new AiAnalysisJob
        {
            SessionId = session.Id, OwnerId = "another-user", State = "Completed",
            Settings = new() { ApiKey = "internal-protected-key-sentinel" },
            Snapshot = new() { EvidenceRelativePath = "private-snapshot-path-sentinel" },
            Result = new() { SessionId = session.Id, IsComplete = true, Overview = "所有用户共享的已完成分析" }
        };
        completed.Result.JobId = completed.Id;
        var queued = new AiAnalysisJob
        {
            SessionId = session.Id, OwnerId = "another-user", ActiveKey = session.Id,
            CreatedAtUtc = completed.CreatedAtUtc.AddSeconds(1),
            Stages = [new() { Id = "Preparing", State = "Running", Progress = 12 }]
        };
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").InsertManyAsync([completed, queued]);
        var account = await fixture.CreateUserAsync("ai-reader@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        var reportPath = $"/api/sessions/{session.Id}/ai-analysis";
        var cancelPath = $"/api/ai/jobs/{queued.Id}/cancel";
        using (var anonymous = await fixture.NewBrowser().Client.GetAsync(reportPath))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS);
        using (var response = await viewer.Client.GetAsync(reportPath))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("internal-protected-key-sentinel", body);
            Assert.DoesNotContain("private-snapshot-path-sentinel", body);
            Assert.DoesNotContain("apiKey", body);
            Assert.DoesNotContain("evidenceRelativePath", body);
            var report = JsonNode.Parse(body)!;
            Assert.False(report["canGenerate"]!.GetValue<bool>());
            Assert.Empty(report["currentJob"]!["stages"]!.AsArray());
            Assert.Equal("所有用户共享的已完成分析", report["latestCompleted"]!["overview"]!.GetValue<string>());
        }
        using (var generation = await viewer.SendAsync(HttpMethod.Post, reportPath, new { force = true }))
        using (var cancellation = await viewer.SendAsync(HttpMethod.Post, cancelPath))
        {
            Assert.Equal(HttpStatusCode.Forbidden, generation.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, cancellation.StatusCode);
        }
        Assert.Equal("Queued", (await ReadJobAsync(fixture, queued.Id)).State);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.GENERATE_ANALYTICS);
        using (var read = await viewer.Client.GetAsync(reportPath))
        using (var generation = await viewer.SendAsync(HttpMethod.Post, reportPath, new { force = false }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, generation.StatusCode);
        }
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS, PermissionIds.GENERATE_ANALYTICS);
        using (var cancellation = await viewer.SendAsync(HttpMethod.Post, cancelPath))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        }
        Assert.Equal("Cancelled", (await ReadJobAsync(fixture, queued.Id)).State);
        using (var read = await viewer.Client.GetAsync("/api/settings"))
        using (var write = await viewer.SendAsync(HttpMethod.Put, "/api/settings", new { }))
        using (var test = await viewer.SendAsync(HttpMethod.Post, "/api/settings/ai/test", new AiSettings
               {
                   Provider = "Custom", BaseUrl = "http://127.0.0.1:1", Model = "local-test-never-called"
               }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, test.StatusCode);
        }
    }

    /// <summary>会话查看者获得与任务页同源的 Worker 阶段和终态，阶段诊断使用公开文案。</summary>
    [MongoHttpFact]
    public async Task SharedAnalysisReturnsPublicWorkerStages()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var session = await SeedSessionAsync(fixture, true);
        var job = new AiAnalysisJob
        {
            SessionId = session.Id, State = "Running", OwnerId = "another-user", WorkerId = "ai-worker", Attempt = 1,
            LeaseUntilUtc = DateTime.UtcNow.AddMinutes(5), Progress = 23,
            Stages =
            [
                new()
                {
                    Id = "Downloading", State = "Completed", Progress = 100, ElapsedMs = 1800,
                    Activity = new() { BytesTransferred = 1024, CurrentFile = "private-worker-file" }
                },
                new()
                {
                    Id = "Analyzing", State = "Running", ElapsedMs = 4000,
                    ErrorCode = "AI_REQUEST_FAILED", Error = "private-worker-error"
                }
            ]
        };
        var jobs = fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs");
        await jobs.InsertOneAsync(job);
        var account = await fixture.CreateUserAsync("ai-stage-reader@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS);
        var path = $"/api/sessions/{session.Id}/ai-analysis";
        using var response = await viewer.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("private-worker-", await response.Content.ReadAsStringAsync());
        var status = (await response.Content.ReadFromJsonAsync<AiAnalysisStatus>())!;
        Assert.False(status.CanGenerate);
        Assert.Equal(job.Id, status.CurrentJob!.Id);
        var stages = status.CurrentJob.Stages!;
        Assert.Equal(new[] { "Downloading", "Analyzing" }, stages.Select(stage => stage.Id));
        Assert.Equal(1024, Assert.Single(stages[0].Metrics).Value);
        Assert.Equal(100, stages[0].Progress);
        Assert.Null(stages[1].Progress);
        Assert.False(string.IsNullOrWhiteSpace(stages[1].Error));

        await jobs.UpdateOneAsync(value => value.Id == job.Id, Builders<AiAnalysisJob>.Update.Set(value => value.State, "Interrupted"));
        var interrupted = (await viewer.Client.GetFromJsonAsync<AiAnalysisStatus>(path))!.CurrentJob!.Stages![1];
        var workerTask = (await fixture.Admin.Client.GetFromJsonAsync<BackgroundTaskSummary>($"/api/admin/worker-tasks/ai/{job.Id}"))!;
        Assert.Equal("Interrupted", interrupted.State);
        Assert.Equal(workerTask.Stages[1].State, interrupted.State);
        Assert.Equal(workerTask.Stages[1].ElapsedMs, interrupted.ElapsedMs);
    }

    /// <summary>管理员密钥写入后加密保存，空输入保留，明确清除生效，读取与保存响应始终脱敏。</summary>
    [MongoHttpFact]
    public async Task AdministratorKeyIsProtectedRetainedClearedAndNeverReturned()
    {
        const string TEST_KEY = "http-test-only-secret-never-sent-to-a-provider";
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!;
        settings.Ai = new()
        {
            Enabled = false, Provider = "DeepSeek", BaseUrl = "http://127.0.0.1:1", Model = "test-only",
            ApiKey = TEST_KEY, TokensPerHour = 1000000, MinimumTokens = 1000000, MaximumTokens = 2000000,
            MaxOutputTokens = 1000000, ContextTokenTarget = 48000, ContextWindowTokens = 2000000, MaxModelRequests = 0, MaxToolCalls = 0,
            RequestTimeoutSeconds = 240, MaxRequestRetries = 3,
            ThinkingMode = "Enabled", ReasoningEffort = "High"
        };
        using (var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var body = await saved.Content.ReadAsStringAsync();
            Assert.DoesNotContain(TEST_KEY, body);
            var response = JsonNode.Parse(body)!;
            Assert.Equal("", response["ai"]!["apiKey"]!.GetValue<string>());
            Assert.True(response["ai"]!["hasApiKey"]!.GetValue<bool>());
            Assert.False(response["ai"]!["clearApiKey"]!.GetValue<bool>());
        }
        var store = fixture.App.Services.GetRequiredService<ISettingsStore>();
        var protectedKey = (await store.GetAsync()).Ai.ApiKey;
        Assert.NotEqual(TEST_KEY, protectedKey);
        Assert.Equal(TEST_KEY, fixture.App.Services.GetRequiredService<ISecretProtector>().Unprotect(protectedKey));
        Assert.DoesNotContain(TEST_KEY, await File.ReadAllTextAsync(fixture.ConfigFile));

        settings = (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!;
        Assert.Equal("", settings.Ai.ApiKey);
        Assert.True(settings.Ai.HasApiKey);
        settings.Ai.Model = "changed-without-replacing-key";
        using (var retained = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
            Assert.DoesNotContain(protectedKey, await retained.Content.ReadAsStringAsync());
        }
        Assert.Equal(protectedKey, (await store.GetAsync()).Ai.ApiKey);
        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        settings = (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!;
        Assert.Equal("", settings.Ai.ApiKey);
        Assert.True(settings.Ai.HasApiKey);
        Assert.Equal("changed-without-replacing-key", settings.Ai.Model);
        Assert.Equal(1000000, settings.Ai.TokensPerHour);
        Assert.Equal(1000000, settings.Ai.MinimumTokens);
        Assert.Equal(2000000, settings.Ai.MaximumTokens);
        Assert.Equal(1000000, settings.Ai.MaxOutputTokens);
        Assert.Equal(48000, settings.Ai.ContextTokenTarget);
        Assert.Equal(2000000, settings.Ai.ContextWindowTokens);
        Assert.Equal(0, settings.Ai.MaxModelRequests);
        Assert.Equal(0, settings.Ai.MaxToolCalls);
        Assert.Equal(240, settings.Ai.RequestTimeoutSeconds);
        Assert.Equal(3, settings.Ai.MaxRequestRetries);
        Assert.Equal("Enabled", settings.Ai.ThinkingMode);
        Assert.Equal("High", settings.Ai.ReasoningEffort);
        store = fixture.App.Services.GetRequiredService<ISettingsStore>();
        Assert.Equal(protectedKey, (await store.GetAsync()).Ai.ApiKey);

        settings.Ai.ClearApiKey = true;
        settings.Ai.ApiKey = "clear-takes-precedence-over-this-test-value";
        using (var cleared = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            var response = (await cleared.Content.ReadFromJsonAsync<ServerSettings>(JsonFiles.Options))!;
            Assert.Equal("", response.Ai.ApiKey);
            Assert.False(response.Ai.HasApiKey);
            Assert.False(response.Ai.ClearApiKey);
        }
        Assert.Equal("", (await store.GetAsync()).Ai.ApiKey);
        Assert.False((await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!.Ai.HasApiKey);
    }

    /// <summary>主站不执行模型，已结束录像可持久排队、去重并取消，任务不持有密钥。</summary>
    [MongoHttpFact]
    public async Task OnlyFinishedRecordingsCreateDeduplicatedJobsWithoutExecutingTheModel()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.Null(fixture.App.Services.GetService<IAiAnalysisRunner>());
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!;
        settings.Ai = new()
        {
            Enabled = true, Provider = "Custom", BaseUrl = "http://127.0.0.1:1", Model = "local-test-never-called",
            ApiKey = "http-test-key-not-for-provider"
        };
        using (var configured = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
        var live = await SeedSessionAsync(fixture, false);
        using (var rejected = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/sessions/{live.Id}/ai-analysis", new { force = false }))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Contains("AI_RECORDING_NOT_FINISHED", await rejected.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").CountDocumentsAsync(FilterDefinition<AiAnalysisJob>.Empty));
        var completed = await SeedSessionAsync(fixture, true);
        var path = $"/api/sessions/{completed.Id}/ai-analysis";
        AiAnalysisJobSummary summary;
        using (var accepted = await fixture.Admin.SendAsync(HttpMethod.Post, path, new { force = false }))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            summary = (await accepted.Content.ReadFromJsonAsync<AiAnalysisJobSummary>())!;
            Assert.Equal("Queued", summary.State);
            Assert.Equal(0, summary.ModelRequests);
        }
        using (var duplicate = await fixture.Admin.SendAsync(HttpMethod.Post, path, new { force = true }))
        {
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            Assert.Equal(summary.Id, (await duplicate.Content.ReadFromJsonAsync<AiAnalysisJobSummary>())!.Id);
        }
        var job = await ReadJobAsync(fixture, summary.Id);
        Assert.Equal("", job.Settings.ApiKey);
        Assert.Equal("Queued", job.State);
        Assert.Equal(0, job.ModelRequests);
        Assert.NotNull(job.Input);
        Assert.Empty(job.Snapshot.Buckets);
        Assert.Equal(0, job.Snapshot.EvidenceCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "ai", "snapshots")));
        using var cancellation = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{summary.Id}/cancel");
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        var cancelled = await ReadJobAsync(fixture, summary.Id);
        Assert.Equal("Cancelled", cancelled.State);
        Assert.Null(cancelled.ActiveKey);
        Assert.Equal(0, cancelled.ModelRequests);
    }

    private static async Task<BroadcastSession> SeedSessionAsync(HttpServerFixture fixture, bool finished)
    {
        var start = DateTime.UtcNow.AddMinutes(-2);
        var session = new BroadcastSession
        {
            Title = "AI HTTP 测试录像", DirectoryPath = "ai-http-" + Guid.NewGuid().ToString("N"),
            StartedAtUtc = start, EndedAtUtc = finished ? start.AddMinutes(1) : null,
            State = finished ? "Completed" : "Recording", HasDanmu = true
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id, RelativePath = "recording.flv", DurationMs = 60000, CommittedBytes = 64, IsClosed = finished
        });
        await using var writer = new SessionEventWriter(Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕"));
        await writer.WriteAsync(new() { OffsetMs = 1000, Text = "【测试同传证据】" });
        return session;
    }

    private static Task<AiAnalysisJob> ReadJobAsync(HttpServerFixture fixture, string id) =>
        fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").Find(item => item.Id == id).SingleAsync();

    private static async Task SetPermissionsAsync(HttpServerFixture fixture, string userId, params string[] permissions)
    {
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/permissions", new PermissionSet(permissions));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
