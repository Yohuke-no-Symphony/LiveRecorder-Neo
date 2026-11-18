using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.AI;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证直播记录生成的独立授权、共享可见性与话题任务隔离。</summary>
public sealed class AiLiveMarkerHttpTests
{
    /// <summary>生成权限默认关闭，上传、管理记录与话题分析授权均不能代替该权限。</summary>
    [MongoHttpFact]
    public async Task GenerationHasItsOwnPermissionAndDoesNotRequireAnalyticsAccess()
    {
        const string GENERATE_MARKERS = "live-markers.generate";
        Assert.Contains(GENERATE_MARKERS, PermissionCatalog.All);
        Assert.DoesNotContain(GENERATE_MARKERS, PermissionCatalog.Defaults);
        Assert.DoesNotContain(GENERATE_MARKERS, PermissionSet.MemberDefaults);
        await using var fixture = await CreateConfiguredFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        var account = await fixture.CreateUserAsync("marker-generator@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        var path = $"/api/sessions/{session.Id}/ai-live-markers";
        using (var anonymous = await fixture.NewBrowser().Client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        foreach (var grants in new[]
                 {
                     new[] { PermissionIds.VIEW_RECORDINGS },
                     new[] { PermissionIds.VIEW_RECORDINGS, PermissionIds.UPLOAD_LIVE_MARKERS, PermissionIds.MANAGE_LIVE_MARKERS },
                     new[] { PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS, PermissionIds.GENERATE_ANALYTICS }
                 })
        {
            await SetPermissionsAsync(fixture, account.Id, grants);
            var status = (await browser.Client.GetFromJsonAsync<AiAnalysisStatus>(path))!;
            Assert.False(status.CanGenerate);
            using var denied = await browser.SendAsync(HttpMethod.Post, path, new { force = true });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await SetPermissionsAsync(fixture, account.Id, GENERATE_MARKERS);
        using (var noView = await browser.Client.GetAsync(path))
        using (var noViewGenerate = await browser.SendAsync(HttpMethod.Post, path, new { force = true }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noView.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, noViewGenerate.StatusCode);
        }

        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, GENERATE_MARKERS);
        Assert.True((await browser.Client.GetFromJsonAsync<AiAnalysisStatus>(path))!.CanGenerate);
        using var accepted = await browser.SendAsync(HttpMethod.Post, path, new { force = true });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var summary = (await accepted.Content.ReadFromJsonAsync<AiAnalysisJobSummary>())!;
        var job = await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").Find(item => item.Id == summary.Id).SingleAsync();
        Assert.Equal("LiveMarkers", job.Purpose);
        Assert.Equal(account.Id, job.OwnerId);
        Assert.Equal("Queued", job.State);
        Assert.Equal(0, job.ModelRequests);
        Assert.Equal("", job.Settings.ApiKey);
        using (var analysis = await browser.Client.GetAsync($"/api/sessions/{session.Id}/ai-analysis"))
        using (var upload = await browser.SendAsync(HttpMethod.Post, $"/api/sessions/{session.Id}/live-markers", new { }))
        using (var settings = await browser.Client.GetAsync("/api/settings"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, analysis.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        }

        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS);
        using (var revoked = await browser.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{job.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        }

        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, GENERATE_MARKERS);
        using var cancellation = await browser.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{job.Id}/cancel");
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
    }

    /// <summary>同场的两种任务各自去重，后台列表和取消操作按任务的实际用途授权。</summary>
    [MongoHttpFact]
    public async Task PurposesAreDeduplicatedListedAndCancelledIndependently()
    {
        await using var fixture = await CreateConfiguredFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        var analysis = await EnqueueAsync(fixture.Admin, $"/api/sessions/{session.Id}/ai-analysis");
        var markers = await EnqueueAsync(fixture.Admin, $"/api/sessions/{session.Id}/ai-live-markers");
        Assert.NotEqual(analysis.Id, markers.Id);
        Assert.Equal(markers.Id, (await EnqueueAsync(fixture.Admin, $"/api/sessions/{session.Id}/ai-live-markers")).Id);
        Assert.Equal(analysis.Id, (await EnqueueAsync(fixture.Admin, $"/api/sessions/{session.Id}/ai-analysis")).Id);
        var account = await fixture.CreateUserAsync("marker-job-reader@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS);
        var visible = (await browser.Client.GetFromJsonAsync<AiAnalysisJobSummary[]>("/api/ai/jobs"))!;
        Assert.Equal(markers.Id, Assert.Single(visible).Id);
        Assert.Equal("LiveMarkers", visible[0].Purpose);

        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, "live-markers.generate");
        using (var denied = await browser.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{analysis.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS,
            PermissionIds.VIEW_ANALYTICS, PermissionIds.GENERATE_ANALYTICS);
        Assert.Equal(2, (await browser.Client.GetFromJsonAsync<AiAnalysisJobSummary[]>("/api/ai/jobs"))!.Length);
        using (var denied = await browser.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{markers.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var cancelled = await browser.SendAsync(HttpMethod.Post, $"/api/ai/jobs/{analysis.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }

        Assert.Equal("Queued", (await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs")
            .Find(item => item.Id == markers.Id).SingleAsync()).State);
        var newerAnalysisJobs = Enumerable.Range(0, 210).Select(index => new AiAnalysisJob
        {
            SessionId = session.Id, State = "Completed", CreatedAtUtc = DateTime.UtcNow.AddSeconds(index + 1)
        });
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").InsertManyAsync(newerAnalysisJobs);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS);
        Assert.Equal(markers.Id, Assert.Single((await browser.Client.GetFromJsonAsync<AiAnalysisJobSummary[]>("/api/ai/jobs"))!).Id);
    }

    /// <summary>共享读取不会入队或请求模型，两种报告与生成文件引用不会相互替换或暴露私有数据。</summary>
    [MongoHttpFact]
    public async Task SharedStatusSeparatesReportsAndNeverStartsGeneration()
    {
        await using var fixture = await CreateConfiguredFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        var analysis = new AiAnalysisJob
        {
            SessionId = session.Id, State = "Completed",
            Result = new() { SessionId = session.Id, IsComplete = true, Overview = "共享话题分析" }
        };
        var markers = new AiAnalysisJob
        {
            SessionId = session.Id, Purpose = "LiveMarkers", State = "Completed",
            LiveMarkerFileId = Guid.NewGuid().ToString("N"),
            Settings = new() { ApiKey = "private-marker-key" },
            Snapshot = new() { EvidenceRelativePath = "private-marker-evidence" },
            Context = new() { ConversationPath = "private-marker-conversation" },
            Stages = [new() { Id = "Reporting", State = "Completed", Progress = 100, ElapsedMs = 1250 }],
            Result = new() { SessionId = session.Id, Purpose = "LiveMarkers", IsComplete = true, Overview = "共享直播记录" }
        };
        analysis.Result.JobId = analysis.Id;
        markers.Result.JobId = markers.Id;
        markers.Result.LiveMarkerFileId = markers.LiveMarkerFileId;
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").InsertManyAsync([analysis, markers]);
        await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs").UpdateOneAsync(item => item.Id == analysis.Id,
            Builders<AiAnalysisJob>.Update.Unset(item => item.Purpose));
        var account = await fixture.CreateUserAsync("marker-status-reader@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, PermissionIds.VIEW_ANALYTICS);
        var topics = (await browser.Client.GetFromJsonAsync<AiAnalysisStatus>($"/api/sessions/{session.Id}/ai-analysis"))!;
        Assert.Equal(analysis.Id, topics.CurrentJob!.Id);
        Assert.Empty(topics.CurrentJob.Stages!);
        Assert.Equal("共享话题分析", topics.LatestCompleted!.Overview);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS);
        for (var index = 0; index < 2; index++)
        {
            using var response = await browser.Client.GetAsync($"/api/sessions/{session.Id}/ai-live-markers");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-marker-", text);
            var status = (await response.Content.ReadFromJsonAsync<AiAnalysisStatus>())!;
            Assert.Equal(markers.Id, status.CurrentJob!.Id);
            var stage = Assert.Single(status.CurrentJob.Stages!);
            Assert.Equal("Reporting", stage.Id);
            Assert.Equal(1250, stage.ElapsedMs);
            Assert.Null(status.CurrentJob.LiveMarkerFileId);
            Assert.Null(status.LatestCompleted!.LiveMarkerFileId);
            Assert.True(status.LatestCompletedStale);
            Assert.Equal("共享直播记录", status.LatestCompleted!.Overview);
            Assert.False(status.CanGenerate);
        }

        var jobs = await fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs")
            .Find(FilterDefinition<AiAnalysisJob>.Empty).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Equal(0, job.ModelRequests));
        Assert.Equal(markers.LiveMarkerFileId, Assert.Single(jobs, job => job.Id == markers.Id).LiveMarkerFileId);
    }

    /// <summary>生成文件沿用共享归属规则，幂等发布保留校准并且不覆盖已有上传记录。</summary>
    [MongoHttpFact]
    public async Task GeneratedFilesPreserveUploadsAndBelongToTheGenerator()
    {
        await using var fixture = await CreateConfiguredFixtureAsync();
        var session = await SeedSessionAsync(fixture);
        var service = fixture.App.Services.GetRequiredService<ISessionLiveMarkerService>();
        var original = Encoding.UTF8.GetBytes($"StartTime: {session.StartedAtUtc:O}\nMarker:\n- RealDateTime: {session.StartedAtUtc.AddSeconds(2):O}\n  LiveTime: 00:00:02\n  Message: 手动记录\n  MarkerColor: Blue\n");
        await using var upload = new MemoryStream(original);
        var manual = await service.UploadAsync(session.Id, upload, "手动记录.yaml", "UTC", 300,
            new("manual-owner", "手动上传者", false));
        var account = await fixture.CreateUserAsync("generated-file-owner@example.test");
        var owner = fixture.NewBrowser();
        await owner.LoginAsync(account.Email, HttpServerFixture.USER_PASSWORD);
        await SetPermissionsAsync(fixture, account.Id, PermissionIds.VIEW_RECORDINGS, "live-markers.generate");
        var summary = await EnqueueAsync(owner, $"/api/sessions/{session.Id}/ai-live-markers");
        var jobs = fixture.Context.Collection<AiAnalysisJob>("ai_analysis_jobs");
        var job = await jobs.Find(item => item.Id == summary.Id).SingleAsync();
        var workspace = Path.Combine(fixture.DirectoryPath, "publication-evidence");
        var input = job.Input!;
        var sources = input.Sources.Select((source, index) => (source.Id, Path: Path.Combine(fixture.StoragePath, job.SourcePaths[index])))
            .ToDictionary(value => value.Id, value => value.Path);
        job.Snapshot = await new AiSnapshotBuilder().BuildAsync(AiExecutionMapper.ToExecution(job), input, sources, workspace);
        var evidence = Assert.Single((await new AiFileEvidenceReader(workspace)
            .ReadAsync(job.Snapshot, 0, job.Snapshot.DurationMs, "All", null, null, 10, CancellationToken.None)).Items);
        job.Context = new()
        {
            Phase = "Completed", Segments = [new() { Index = 0, StartMs = 0, EndMs = 120000, State = "Completed" }]
        };
        job.Result.IsComplete = true;
        job.Result.LiveMarkers = [new()
        {
            Id = "s1:music", Message = "观众称赞歌曲", OffsetMs = evidence.OffsetMs,
            AnchorEvidenceId = evidence.Id, Evidence = [evidence]
        }];
        var publisher = fixture.App.Services.GetRequiredService<AiLiveMarkerPublisher>();
        var fileId = await publisher.PublishAsync(job, CancellationToken.None);
        job.LiveMarkerFileId = fileId;
        job.Result.LiveMarkerFileId = fileId;
        job.State = "Completed";
        job.ActiveKey = null;
        await jobs.ReplaceOneAsync(item => item.Id == job.Id, job);
        var path = $"/api/sessions/{session.Id}/live-markers";
        var list = (await owner.Client.GetFromJsonAsync<LiveMarkerFileListResponse>(path))!;
        Assert.Equal(2, list.Files.Count);
        var generated = Assert.Single(list.Files, file => file.Id == fileId);
        Assert.Equal(account.Id, generated.UploaderId);
        Assert.Equal(account.DisplayName, generated.UploaderName);
        Assert.True(generated.CanManage);
        var detail = (await owner.Client.GetFromJsonAsync<LiveMarkerFileResponse>(path + "/" + fileId))!;
        Assert.Equal(evidence.OffsetMs, Assert.Single(detail.Entries).OffsetMs, 3);
        using (var aligned = await owner.SendAsync(HttpMethod.Put, path + "/" + fileId + "/alignment", new LiveMarkerAlignmentRequest(500)))
        {
            Assert.Equal(HttpStatusCode.OK, aligned.StatusCode);
        }

        Assert.Equal(fileId, await publisher.PublishAsync(job, CancellationToken.None));
        Assert.Equal(500, (await owner.Client.GetFromJsonAsync<LiveMarkerFileResponse>(path + "/" + fileId))!.File.CalibrationMs);
        var viewerAccount = await fixture.CreateUserAsync("generated-file-viewer@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(viewerAccount.Email, HttpServerFixture.USER_PASSWORD);
        var shared = (await viewer.Client.GetFromJsonAsync<LiveMarkerFileResponse>(path + "/" + fileId))!;
        Assert.False(shared.File.CanManage);
        Assert.Equal("观众称赞歌曲", Assert.Single(shared.Entries).Message);
        using (var denied = await viewer.SendAsync(HttpMethod.Delete, path + "/" + fileId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var deleted = await owner.SendAsync(HttpMethod.Delete, path + "/" + fileId))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var status = await owner.Client.GetAsync($"/api/sessions/{session.Id}/ai-live-markers"))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        var remaining = Assert.Single((await owner.Client.GetFromJsonAsync<LiveMarkerFileListResponse>(path))!.Files);
        Assert.Equal(manual.Id, remaining.Id);
        Assert.Equal(300, remaining.CalibrationMs);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(fixture.StoragePath, session.DirectoryPath,
            LiveMarkerFileStore.DIRECTORY_NAME, manual.Id, LiveMarkerFileStore.ORIGINAL_FILE_NAME)));
        var regenerated = await EnqueueAsync(owner, $"/api/sessions/{session.Id}/ai-live-markers");
        Assert.NotEqual(job.Id, regenerated.Id);
    }

    private static async Task<HttpServerFixture> CreateConfiguredFixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync();
        try
        {
            await fixture.InstallAsync();
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            Assert.Null(fixture.App.Services.GetService<IAiAnalysisRunner>());
            var settings = (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!;
            settings.Ai = new()
            {
                Enabled = true, Provider = "Custom", BaseUrl = "http://127.0.0.1:1", Model = "test-never-called",
                ApiKey = "local-test-only-never-sent"
            };
            using var configured = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task<BroadcastSession> SeedSessionAsync(HttpServerFixture fixture)
    {
        var session = new BroadcastSession
        {
            Title = "生成直播记录测试", DirectoryPath = "marker-generation-" + Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-3), EndedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            State = "Completed", HasDanmu = true
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            SessionId = session.Id, RelativePath = "recording.flv", DurationMs = 120000, CommittedBytes = 64, IsClosed = true
        });
        await using var writer = new SessionEventWriter(Path.Combine(fixture.StoragePath, session.DirectoryPath, "弹幕"));
        await writer.WriteAsync(new() { Kind = "Danmu", OffsetMs = 1000, Text = "这首歌好听" });
        return session;
    }

    private static async Task<AiAnalysisJobSummary> EnqueueAsync(HttpBrowser browser, string path)
    {
        using var response = await browser.SendAsync(HttpMethod.Post, path, new { force = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AiAnalysisJobSummary>())!;
    }

    private static async Task SetPermissionsAsync(HttpServerFixture fixture, string userId, params string[] permissions)
    {
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/permissions", new PermissionSet(permissions));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
