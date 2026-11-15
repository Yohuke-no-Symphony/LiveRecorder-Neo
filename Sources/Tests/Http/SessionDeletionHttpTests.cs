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

    /// <summary>匿名和未授权成员不能删除场次，也不能查看或操作共享录像回收记录。</summary>
    [MongoHttpFact]
    public async Task SessionDeletionAndSessionTrashRequireExplicitPermission()
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

    /// <summary>管理录像权限可恢复共享回收项和设置永久保留，撤权对既有 Cookie 立即生效。</summary>
    [MongoHttpFact]
    public async Task ManagePermissionAllowsSharedTrashRestoreAndRetentionButNotPurgeOrAnalytics()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("recording-manager@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var manifest = await SeedAsync(fixture);
        await SetPermissionsAsync(fixture, user.Id, manage: true);

        using var retained = await browser.SendAsync(HttpMethod.Put, "/api/sessions/" + manifest.Session.Id + "/retention", new { isPermanent = true });
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        var detail = await browser.Client.GetFromJsonAsync<JsonObject>("/api/sessions/" + manifest.Session.Id);
        Assert.True(detail!["session"]!["isPermanent"]!.GetValue<bool>());
        var saved = await File.ReadAllTextAsync(Path.Combine(fixture.StoragePath, manifest.Session.DirectoryPath, "场次.json"));
        Assert.True(JsonNode.Parse(saved)!["session"]!["isPermanent"]!.GetValue<bool>());
        using var analysis = await browser.Client.GetAsync("/api/sessions/" + manifest.Session.Id + "/analytics");
        Assert.Equal(HttpStatusCode.Forbidden, analysis.StatusCode);

        using var deletedByAdmin = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deletedByAdmin.StatusCode);
        var entry = await fixture.Context.Collection<TrashEntry>("trash").Find(x => x.EntityId == manifest.Session.Id).SingleAsync();
        var visible = await browser.Client.GetFromJsonAsync<JsonArray>("/api/trash");
        Assert.Contains(visible!, item => item!["id"]!.GetValue<string>() == entry.Id);
        using var restored = await browser.SendAsync(HttpMethod.Post, "/api/trash/" + entry.Id + "/restore");
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Contains(manifest.Session.Id, await SessionIdsAsync(browser));

        using var batch = await browser.SendAsync(HttpMethod.Post, "/api/sessions/trash", new { sessionIds = new[] { manifest.Session.Id } });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.True(Assert.Single((await batch.Content.ReadFromJsonAsync<JsonArray>())!)!["success"]!.GetValue<bool>());
        entry = await fixture.Context.Collection<TrashEntry>("trash").Find(x => x.EntityId == manifest.Session.Id).SingleAsync();
        using var purge = await browser.SendAsync(HttpMethod.Delete, "/api/trash/" + entry.Id);
        Assert.Equal(HttpStatusCode.Forbidden, purge.StatusCode);

        await SetPermissionsAsync(fixture, user.Id);
        using var revokedRestore = await browser.SendAsync(HttpMethod.Post, "/api/trash/" + entry.Id + "/restore");
        using var revokedRetention = await browser.SendAsync(HttpMethod.Put, "/api/sessions/" + manifest.Session.Id + "/retention", new { isPermanent = false });
        using var revokedBatch = await browser.SendAsync(HttpMethod.Post, "/api/sessions/trash", new { sessionIds = new[] { manifest.Session.Id } });
        Assert.Equal(HttpStatusCode.Forbidden, revokedRestore.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revokedRetention.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revokedBatch.StatusCode);
        visible = await browser.Client.GetFromJsonAsync<JsonArray>("/api/trash");
        Assert.DoesNotContain(visible!, item => item!["id"]!.GetValue<string>() == entry.Id);
    }

    /// <summary>仅彻删权限可处理共享录像回收项，但不能软删、恢复、标记永久保留或访问其他人的切片。</summary>
    [MongoHttpFact]
    public async Task PurgePermissionIsIndependentAndPreservesPrivateClipOwnership()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("recording-purger@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        await SetPermissionsAsync(fixture, user.Id, purge: true);
        var manifest = await SeedAsync(fixture);
        using var forbiddenDelete = await browser.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        using var forbiddenRetention = await browser.SendAsync(HttpMethod.Put, "/api/sessions/" + manifest.Session.Id + "/retention", new { isPermanent = true });
        using var forbiddenAnalytics = await browser.Client.GetAsync("/api/sessions/" + manifest.Session.Id + "/analytics");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRetention.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAnalytics.StatusCode);
        using var deletedByAdmin = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deletedByAdmin.StatusCode);
        var entry = await fixture.Context.Collection<TrashEntry>("trash").Find(x => x.EntityId == manifest.Session.Id).SingleAsync();
        var clip = new Clip { OwnerId = "another-viewer", Title = "Private clip", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) };
        var privateTrash = new TrashEntry { OwnerId = clip.OwnerId, EntityId = clip.Id, Kind = "Clip", State = "Active", PurgeAtUtc = DateTime.UtcNow.AddDays(7) };
        await fixture.Context.Collection<Clip>("clips").InsertOneAsync(clip);
        await fixture.Context.Collection<TrashEntry>("trash").InsertOneAsync(privateTrash);
        var visible = await browser.Client.GetFromJsonAsync<JsonArray>("/api/trash");
        Assert.Contains(visible!, item => item!["id"]!.GetValue<string>() == entry.Id);
        Assert.DoesNotContain(visible!, item => item!["id"]!.GetValue<string>() == privateTrash.Id);
        using var forbiddenRestore = await browser.SendAsync(HttpMethod.Post, "/api/trash/" + entry.Id + "/restore");
        using var forbiddenClipDelete = await browser.SendAsync(HttpMethod.Delete, "/api/clips/" + clip.Id);
        using var forbiddenClipPurge = await browser.SendAsync(HttpMethod.Delete, "/api/trash/" + privateTrash.Id);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRestore.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenClipDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenClipPurge.StatusCode);
        using var purged = await browser.SendAsync(HttpMethod.Delete, "/api/trash/" + entry.Id);
        Assert.Equal(HttpStatusCode.NoContent, purged.StatusCode);
        Assert.False(await fixture.Context.Collection<BroadcastSession>("sessions").Find(x => x.Id == manifest.Session.Id).AnyAsync());
        Assert.False(Directory.Exists(Path.Combine(fixture.StoragePath, entry.TrashRelativePath)));
        Assert.True(await fixture.Context.Collection<Clip>("clips").Find(x => x.Id == clip.Id).AnyAsync());
    }

    /// <summary>分析权限单独授予和撤销，管理员仍可在未配置个人权限时操作录像和分析。</summary>
    [MongoHttpFact]
    public async Task AnalyticsPermissionCanBeGrantedAndRevokedWithoutGrantingRecordingManagement()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("recording-analyst@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var manifest = await SeedAsync(fixture);
        await SetPermissionsAsync(fixture, user.Id, analytics: true);
        using var granted = await browser.Client.GetAsync("/api/sessions/" + manifest.Session.Id + "/analytics");
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        using var forbiddenDelete = await browser.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        using var forbiddenRetention = await browser.SendAsync(HttpMethod.Put, "/api/sessions/" + manifest.Session.Id + "/retention", new { isPermanent = true });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRetention.StatusCode);
        await SetPermissionsAsync(fixture, user.Id);
        using var revoked = await browser.Client.GetAsync("/api/sessions/" + manifest.Session.Id + "/analytics");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var adminRetained = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/sessions/" + manifest.Session.Id + "/retention", new { isPermanent = true });
        using var adminAnalysis = await fixture.Admin.Client.GetAsync("/api/sessions/" + manifest.Session.Id + "/analytics");
        Assert.Equal(HttpStatusCode.OK, adminRetained.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminAnalysis.StatusCode);
    }

    /// <summary>录像和回收站周期可独立关闭，并由服务器拒绝负数及超出上限的周期。</summary>
    [MongoHttpFact]
    public async Task RetentionSettingsAcceptDisabledAndMaximumCyclesButRejectInvalidValues()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(0, settings!["recordingRetentionDays"]!.GetValue<int>());
        Assert.Equal(7, settings["trashRetentionDays"]!.GetValue<int>());
        foreach (var days in new[] { 0, 3650 })
        {
            settings["recordingRetentionDays"] = days;
            settings["trashRetentionDays"] = days;
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
            Assert.Equal(days, saved!["recordingRetentionDays"]!.GetValue<int>());
            Assert.Equal(days, saved["trashRetentionDays"]!.GetValue<int>());
        }
        foreach (var (recordingDays, trashDays) in new[] { (-1, 7), (3651, 7), (0, -1), (0, 3651) })
        {
            settings["recordingRetentionDays"] = recordingDays;
            settings["trashRetentionDays"] = trashDays;
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>普通成员可读取当前周期推导的列表与详情期限，无需访问管理员设置，录制中录像没有期限。</summary>
    [MongoHttpFact]
    public async Task MembersReadCurrentExpirationWithoutSettingsOrManagementPermissions()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var recorder = fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single();
        await recorder.StopAsync(CancellationToken.None);
        var completed = await SeedAsync(fixture);
        var recording = await SeedAsync(fixture, true);
        var user = await fixture.CreateUserAsync("expiration-viewer@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var forbiddenSettings = await browser.Client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenSettings.StatusCode);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");

        foreach (var days in new[] { 0, 7, 30, 0 })
        {
            settings!["recordingRetentionDays"] = days;
            using var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            await AssertSessionExpirationAsync(browser, completed.Session.Id,
                days == 0 ? null : completed.Session.EndedAtUtc!.Value.AddDays(days));
            await AssertSessionExpirationAsync(browser, recording.Session.Id, null);
        }
    }

    /// <summary>取消保护和恢复均返回新的保留期限，并同步到列表与详情。</summary>
    [MongoHttpFact]
    public async Task ProtectionResponsesAndRestorationKeepProjectedExpirationCurrent()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var manifest = await SeedAsync(fixture);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        settings!["recordingRetentionDays"] = 7;
        using var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var originalExpiration = manifest.Session.EndedAtUtc!.Value.AddDays(7);
        await AssertSessionExpirationAsync(fixture.Admin, manifest.Session.Id, originalExpiration);

        using var retained = await fixture.Admin.SendAsync(HttpMethod.Put,
            "/api/sessions/" + manifest.Session.Id + "/retention", new { isPermanent = true });
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        var retainedSession = await retained.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(manifest.Session.Id, retainedSession!["id"]!.GetValue<string>());
        Assert.Equal(manifest.Session.Title, retainedSession["title"]!.GetValue<string>());
        Assert.True(retainedSession["isPermanent"]!.GetValue<bool>());
        AssertProjectedExpiration(retainedSession, null);
        await AssertSessionExpirationAsync(fixture.Admin, manifest.Session.Id, null);

        var beforeCancellation = DateTime.UtcNow;
        using var unprotected = await fixture.Admin.SendAsync(HttpMethod.Put,
            "/api/sessions/" + manifest.Session.Id + "/retention", new { isPermanent = false });
        Assert.Equal(HttpStatusCode.OK, unprotected.StatusCode);
        var unprotectedSession = await unprotected.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(manifest.Session.Id, unprotectedSession!["id"]!.GetValue<string>());
        Assert.False(unprotectedSession["isPermanent"]!.GetValue<bool>());
        var cancelledAt = unprotectedSession["retentionStartedAtUtc"]!.GetValue<DateTime>();
        Assert.InRange(cancelledAt, beforeCancellation, DateTime.UtcNow);
        var cancellationExpiration = cancelledAt.AddDays(7);
        Assert.True(cancellationExpiration > originalExpiration);
        AssertProjectedExpiration(unprotectedSession, cancellationExpiration);
        await AssertSessionExpirationAsync(fixture.Admin, manifest.Session.Id, cancellationExpiration);

        using var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, "/api/sessions/" + manifest.Session.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var entry = await fixture.Context.Collection<TrashEntry>("trash")
            .Find(x => x.EntityId == manifest.Session.Id).SingleAsync();
        using var restored = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/trash/" + entry.Id + "/restore");
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        var restoredSession = await fixture.Context.Collection<BroadcastSession>("sessions")
            .Find(x => x.Id == manifest.Session.Id).SingleAsync();
        Assert.NotNull(restoredSession.RetentionStartedAtUtc);
        var restoredExpiration = restoredSession.RetentionStartedAtUtc.Value.AddDays(7);
        Assert.True(restoredExpiration > originalExpiration);
        await AssertSessionExpirationAsync(fixture.Admin, manifest.Session.Id, restoredExpiration);
    }

    private static async Task AssertSessionExpirationAsync(HttpBrowser browser, string id, DateTime? expiresAtUtc)
    {
        var sessions = await browser.Client.GetFromJsonAsync<JsonArray>("/api/sessions");
        Assert.NotNull(sessions);
        var listed = Assert.Single(sessions, item => item!["id"]!.GetValue<string>() == id)!.AsObject();
        AssertProjectedExpiration(listed, expiresAtUtc);
        var detail = await browser.Client.GetFromJsonAsync<JsonObject>("/api/sessions/" + id);
        Assert.NotNull(detail);
        Assert.Equal(id, detail["session"]!["id"]!.GetValue<string>());
        AssertProjectedExpiration(detail["session"]!.AsObject(), expiresAtUtc);
    }

    private static void AssertProjectedExpiration(JsonObject session, DateTime? expiresAtUtc)
    {
        Assert.True(session.ContainsKey("expiresAtUtc"));
        if (expiresAtUtc is { } expected)
        {
            Assert.NotNull(session["expiresAtUtc"]);
            var actual = session["expiresAtUtc"]!.GetValue<DateTime>();
            Assert.InRange(actual, expected.AddMilliseconds(-1), expected.AddMilliseconds(1));
        }
        else
        {
            Assert.Null(session["expiresAtUtc"]);
        }
    }

    private static async Task SetPermissionsAsync(HttpServerFixture fixture, string id, bool manage = false, bool analytics = false, bool purge = false)
    {
        var permissions = PermissionSet.MemberDefaults.Concat(new[]
        {
            (PermissionIds.MANAGE_RECORDINGS, manage), (PermissionIds.VIEW_ANALYTICS, analytics), (PermissionIds.PURGE_RECORDINGS, purge)
        }.Where(item => item.Item2).Select(item => item.Item1)).ToArray();
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/admin/users/" + id + "/permissions", permissions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
