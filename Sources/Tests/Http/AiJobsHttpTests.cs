using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证共享 AI 后台任务的可见性、权限和公开字段投影。</summary>
public sealed class AiJobsHttpTests
{
    /// <summary>查看权限可以读取其他人发起的任务，凭据与私有上下文不外泄，取消仍单独授权。</summary>
    [MongoHttpFact]
    public async Task ListingSharesPublicProgressWithoutGrantingCancellation()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await fixture.App.Services.GetServices<IHostedService>().OfType<AiAnalysisWorker>().Single().StopAsync(CancellationToken.None);
        var session = new BroadcastSession { Title = "后台 AI 任务测试", StartedAtUtc = DateTime.UtcNow.AddHours(-2), EndedAtUtc = DateTime.UtcNow };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        var job = new AiAnalysisJob
        {
            SessionId = session.Id, OwnerId = "someone-else", State = "Queued", ActiveKey = session.Id,
            Settings = new() { ApiKey = "private-key-must-not-appear" },
            Snapshot = new() { Title = "old-title", EvidenceRelativePath = "private-evidence-path" },
            PromptTokens = 1240000, CompletionTokens = 60000, ModelRequests = 56, ToolCalls = 108, Progress = 50,
            Context = new()
            {
                Phase = "Analyzing", CurrentSegmentIndex = 1, ContextCompactions = 8, ConversationPath = "private-context-path",
                Segments = [new() { Index = 0, State = "Completed", StartMs = 0, EndMs = 1800000 }, new() { Index = 1, StartMs = 1800000, EndMs = 3600000 }]
            }
        };
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").InsertOneAsync(job);
        var account = await fixture.CreateUserAsync("ai-jobs-reader@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        using (var anonymous = await fixture.NewBrowser().Client.GetAsync("/api/ai/jobs"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        foreach (var permissions in new[] { Array.Empty<string>(), new[] { PermissionIds.VIEW_ANALYTICS } })
        {
            using var grant = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{account.Id}/permissions", new PermissionSet(permissions));
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
            using var denied = await viewer.Client.GetAsync("/api/ai/jobs");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var grant = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{account.Id}/permissions",
                   new PermissionSet([PermissionIds.VIEW_RECORDINGS])))
        {
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        }
        Assert.Empty((await viewer.Client.GetFromJsonAsync<AiAnalysisJobSummary[]>("/api/ai/jobs"))!);
        using (var grant = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{account.Id}/permissions",
                   new PermissionSet([PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS])))
        {
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        }
        using (var response = await viewer.Client.GetAsync("/api/ai/jobs"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-", text);
            Assert.DoesNotContain("ownerId", text);
            Assert.DoesNotContain("apiKey", text);
            Assert.DoesNotContain("conversationPath", text);
            var row = Assert.Single((await response.Content.ReadFromJsonAsync<AiAnalysisJobSummary[]>())!);
            Assert.Equal(job.Id, row.Id);
            Assert.Equal(session.Title, row.Title);
            Assert.Equal(1300000, row.PromptTokens + row.CompletionTokens);
            Assert.Equal(2, row.TotalSegments);
            Assert.Equal(1, row.CompletedSegments);
            Assert.Equal(2, row.CurrentSegment);
            Assert.Equal(8, row.ContextCompactions);
            Assert.False(row.CancelRequested);
        }
        using (var denied = await viewer.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{job.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var grant = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{account.Id}/permissions",
                   new PermissionSet([PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS, PermissionIds.GENERATE_ANALYTICS])))
        {
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        }
        using (var cancelled = await viewer.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{job.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }
        var finished = Assert.Single((await viewer.Client.GetFromJsonAsync<AiAnalysisJobSummary[]>("/api/ai/jobs"))!);
        Assert.Equal("Cancelled", finished.State);
        Assert.True(finished.CancelRequested);
    }

    /// <summary>先排除不可见或已删除来源再限制数量，历史列表不会被隐藏任务占满。</summary>
    [MongoHttpFact]
    public async Task VisibilityIsAppliedBeforeLatestTwoHundredLimit()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var visible = new BroadcastSession { Title = "可见录像" };
        var hidden = new BroadcastSession { IsHidden = true };
        var trashed = new BroadcastSession { IsTrashed = true };
        var pending = new BroadcastSession { ProjectionReady = false };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertManyAsync([visible, hidden, trashed, pending]);
        var now = DateTime.UtcNow;
        var jobs = Enumerable.Range(0, 210).Select(index => new AiAnalysisJob
        {
            SessionId = visible.Id, State = "Completed", CreatedAtUtc = now.AddMinutes(index - 210)
        }).ToList();
        var newest = jobs[^1].Id;
        jobs.AddRange(new[] { hidden.Id, trashed.Id, pending.Id, "missing-session" }.Select(id => new AiAnalysisJob
        {
            SessionId = id, State = "Completed", CreatedAtUtc = now
        }));
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").InsertManyAsync(jobs);
        var listed = (await fixture.Admin.Client.GetFromJsonAsync<AiAnalysisJobSummary[]>("/api/ai/jobs"))!;
        Assert.Equal(200, listed.Length);
        Assert.Equal(newest, listed[0].Id);
        Assert.All(listed, row => Assert.Equal(visible.Id, row.SessionId));
        Assert.Equal(listed.OrderByDescending(row => row.CreatedAtUtc).Select(row => row.Id), listed.Select(row => row.Id));
    }
}
