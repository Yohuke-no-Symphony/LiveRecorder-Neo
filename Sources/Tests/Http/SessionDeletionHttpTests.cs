using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过真实 HTTP 和隔离媒体文件验证录播回收、批量结果、权限及重启恢复。</summary>
public sealed class SessionDeletionHttpTests
{
    /// <summary>管理员删除场次后立即隐藏录播，重复删除不会创建多个回收条目。</summary>
    [MongoHttpFact]
    public async Task AdministratorCanTrashOneSessionAndRepeatedDeleteIsIdempotent()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var manifest = await SeedAsync(fixture);
        var source = MediaFile(fixture, manifest);
        var contents = await File.ReadAllTextAsync(source);
        Assert.Contains(manifest.Session.Id, await SessionIdsAsync(fixture.Admin));

        using var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.DoesNotContain(manifest.Session.Id, await SessionIdsAsync(fixture.Admin));
        using var detail = await fixture.Admin.Client.GetAsync("/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        var entry = await fixture.Context.Collection<TrashEntry>("trash").Find(x => x.EntityId == manifest.Session.Id).SingleAsync();
        Assert.Equal("Session", entry.Kind);
        Assert.False(File.Exists(source));
        Assert.Equal(contents, await File.ReadAllTextAsync(Path.Combine(fixture.StoragePath, entry.TrashRelativePath, manifest.Segments[0].RelativePath)));

        using var repeated = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(1, await fixture.Context.Collection<TrashEntry>("trash").CountDocumentsAsync(x => x.EntityId == manifest.Session.Id));
    }

    /// <summary>批量删除分别报告已结束、在录和不存在的场次，只隐藏成功项。</summary>
    [MongoHttpFact]
    public async Task BatchReportsPartialSuccessWithoutDeletingRecordingOrMissingSessions()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var recorder = fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single();
        await recorder.StopAsync(CancellationToken.None);
        var completed = await SeedAsync(fixture);
        var recording = await SeedAsync(fixture, true);
        var missingId = "missing-session-" + Guid.NewGuid().ToString("N");

        using var activeDelete = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + recording.Session.Id);
        Assert.Equal(HttpStatusCode.Conflict, activeDelete.StatusCode);
        using var missingDelete = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + missingId);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/trash", new { sessionIds = new[] { completed.Session.Id, recording.Session.Id, missingId } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<JsonArray>();
        Assert.Equal(3, results!.Count);
        var byId = results.Select(item => item!.AsObject()).ToDictionary(item => item["sessionId"]!.GetValue<string>());
        Assert.True(byId[completed.Session.Id]["success"]!.GetValue<bool>());
        foreach (var id in new[] { recording.Session.Id, missingId })
        {
            Assert.False(byId[id]["success"]!.GetValue<bool>());
            Assert.False(string.IsNullOrWhiteSpace(byId[id]["error"]!.GetValue<string>()));
        }
        var visible = await SessionIdsAsync(fixture.Admin);
        Assert.DoesNotContain(completed.Session.Id, visible);
        Assert.Contains(recording.Session.Id, visible);
        Assert.True(File.Exists(MediaFile(fixture, recording)));
        Assert.False((await fixture.Context.Collection<BroadcastSession>("sessions").Find(x => x.Id == recording.Session.Id).SingleAsync()).IsTrashed);
        Assert.Equal(1, await fixture.Context.Collection<TrashEntry>("trash").CountDocumentsAsync(x => x.Kind == "Session"));
    }

    /// <summary>批量参数必须包含一到二百个非空场次标识，边界内的重复项只处理一次。</summary>
    [MongoHttpFact]
    public async Task BatchValidatesOneToTwoHundredIdentifiersBeforeProcessing()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var invalidRequests = new object[]
        {
            new { },
            new { sessionIds = (string[]?)null },
            new { sessionIds = Array.Empty<string>() },
            new { sessionIds = new[] { " " } },
            new { sessionIds = new[] { "valid-id", "" } },
            new { sessionIds = Enumerable.Repeat("missing-session", 201).ToArray() }
        };
        foreach (var request in invalidRequests)
        {
            using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/trash", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("LIBRARY_DELETE_COUNT_INVALID", problem!["code"]!.GetValue<string>());
        }
        foreach (var count in new[] { 1, 200 })
        {
            using var response = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/sessions/trash", new { sessionIds = Enumerable.Repeat("missing-session", count).ToArray() });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var results = await response.Content.ReadFromJsonAsync<JsonArray>();
            var result = Assert.Single(results!);
            Assert.Equal("missing-session", result!["sessionId"]!.GetValue<string>());
            Assert.False(result["success"]!.GetValue<bool>());
        }
        Assert.Equal(0, await fixture.Context.Collection<TrashEntry>("trash").CountDocumentsAsync(_ => true));
    }

    /// <summary>匿名和普通成员不能删除场次，也不能查看或操作管理员的场次回收记录。</summary>
    [MongoHttpFact]
    public async Task SessionDeletionAndSessionTrashRequireAdministrator()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var manifest = await SeedAsync(fixture);
        var browser = fixture.NewBrowser();
        await browser.RefreshCsrfAsync();
        using var anonymousSingle = await browser.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        using var anonymousBatch = await browser.SendAsync(HttpMethod.Post, "/api/sessions/trash", new { sessionIds = new[] { manifest.Session.Id } });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousSingle.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousBatch.StatusCode);

        var user = await fixture.CreateUserAsync("session-deletion-viewer@example.test");
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var memberSingle = await browser.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        using var memberBatch = await browser.SendAsync(HttpMethod.Post, "/api/sessions/trash", new { sessionIds = new[] { manifest.Session.Id } });
        Assert.Equal(HttpStatusCode.Forbidden, memberSingle.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberBatch.StatusCode);
        Assert.Contains(manifest.Session.Id, await SessionIdsAsync(browser));
        Assert.True(File.Exists(MediaFile(fixture, manifest)));

        using var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var entry = await fixture.Context.Collection<TrashEntry>("trash").Find(x => x.EntityId == manifest.Session.Id).SingleAsync();
        var visibleTrash = await browser.Client.GetFromJsonAsync<JsonArray>("/api/trash");
        Assert.DoesNotContain(visibleTrash!, item => item!["id"]!.GetValue<string>() == entry.Id);
        using var memberRestore = await browser.SendAsync(HttpMethod.Post, "/api/trash/" + entry.Id + "/restore");
        using var memberPurge = await browser.SendAsync(HttpMethod.Delete, "/api/trash/" + entry.Id);
        Assert.Equal(HttpStatusCode.Forbidden, memberRestore.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberPurge.StatusCode);
        Assert.True((await fixture.Context.Collection<BroadcastSession>("sessions").Find(x => x.Id == manifest.Session.Id).SingleAsync()).IsTrashed);
        Assert.True(Directory.Exists(Path.Combine(fixture.StoragePath, entry.TrashRelativePath)));
    }

    /// <summary>重启不导入回收目录，恢复后媒体和场次清单在再次重启后保持可见。</summary>
    [MongoHttpFact]
    public async Task TrashedSessionStaysHiddenAfterRestartAndRestoreSurvivesAnotherRestart()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var manifest = await SeedAsync(fixture);
        var source = MediaFile(fixture, manifest);
        var contents = await File.ReadAllTextAsync(source);
        using var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var entry = await fixture.Context.Collection<TrashEntry>("trash").Find(x => x.EntityId == manifest.Session.Id).SingleAsync();

        await RestartAndWaitForRebuildAsync(fixture);
        Assert.DoesNotContain(manifest.Session.Id, await SessionIdsAsync(fixture.Admin));
        using var hiddenDetail = await fixture.Admin.Client.GetAsync("/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NotFound, hiddenDetail.StatusCode);
        var trash = await fixture.Admin.Client.GetFromJsonAsync<JsonArray>("/api/trash");
        Assert.Contains(trash!, item => item!["id"]!.GetValue<string>() == entry.Id && item["kind"]!.GetValue<string>() == "Session");
        Assert.False(File.Exists(source));

        using var restored = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/trash/" + entry.Id + "/restore");
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Contains(manifest.Session.Id, await SessionIdsAsync(fixture.Admin));
        Assert.Equal(contents, await File.ReadAllTextAsync(source));
        Assert.Equal(0, await fixture.Context.Collection<TrashEntry>("trash").CountDocumentsAsync(x => x.EntityId == manifest.Session.Id));

        await RestartAndWaitForRebuildAsync(fixture);
        Assert.Contains(manifest.Session.Id, await SessionIdsAsync(fixture.Admin));
        var detail = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/sessions/" + manifest.Session.Id);
        Assert.False(detail!["session"]!["isTrashed"]!.GetValue<bool>());
        Assert.Single(detail["segments"]!.AsArray());
        Assert.Equal(contents, await File.ReadAllTextAsync(source));
    }

    private static async Task RestartAndWaitForRebuildAsync(HttpServerFixture fixture)
    {
        var recorder = fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single();
        await recorder.StopAsync(CancellationToken.None);
        var probe = await SeedAsync(fixture, publishToDatabase: false);
        await fixture.RestartAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await fixture.Context.Collection<BroadcastSession>("sessions").Find(x => x.Id == probe.Session.Id).AnyAsync(timeout.Token))
        {
            await Task.Delay(25, timeout.Token);
        }
        using var deletedProbe = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + probe.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deletedProbe.StatusCode);
    }

    private static async Task<SessionManifest> SeedAsync(HttpServerFixture fixture, bool recording = false, bool publishToDatabase = true)
    {
        var startedAt = DateTime.UtcNow.AddHours(-1);
        var session = new BroadcastSession
        {
            StreamerId = "http-deletion-streamer-" + Guid.NewGuid().ToString("N"),
            RoomId = 42,
            Title = "HTTP deletion fixture",
            StartedAtUtc = startedAt,
            EndedAtUtc = recording ? null : startedAt.AddMinutes(5),
            LastMediaAtUtc = startedAt.AddMinutes(5),
            State = recording ? "Recording" : "Completed"
        };
        session.DirectoryPath = Path.Combine("http-session-deletion", session.Id);
        var relative = Path.Combine("source", "fixture.flv");
        var file = Path.Combine(fixture.StoragePath, session.DirectoryPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "http-session-data-" + session.Id);
        session.TotalBytes = new FileInfo(file).Length;
        var segment = new RecordingSegment
        {
            SessionId = session.Id,
            RelativePath = relative,
            DurationMs = TimeSpan.FromMinutes(5).TotalMilliseconds,
            CommittedBytes = session.TotalBytes,
            IsClosed = !recording
        };
        var manifest = new SessionManifest { Session = session, Segments = [segment] };
        if (publishToDatabase)
        {
            await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
            await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(segment);
        }
        await JsonFiles.WriteAsync(Path.Combine(fixture.StoragePath, session.DirectoryPath, "场次.json"), manifest);
        return manifest;
    }

    private static string MediaFile(HttpServerFixture fixture, SessionManifest manifest) => Path.Combine(fixture.StoragePath, manifest.Session.DirectoryPath, manifest.Segments[0].RelativePath);

    private static async Task<string[]> SessionIdsAsync(HttpBrowser browser)
    {
        var sessions = await browser.Client.GetFromJsonAsync<JsonArray>("/api/sessions");
        Assert.NotNull(sessions);
        return [.. sessions.Select(item => item!["id"]!.GetValue<string>())];
    }
}
