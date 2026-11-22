using System.Net;
using System.Net.Http.Json;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证全站切片权限、个人归属和回收站管理的资源隔离。</summary>
public sealed class ClipManagementHttpTests
{
    /// <summary>有效切片按账号或全站范围查询，管理分页在同一创建时间下仍有稳定顺序。</summary>
    [MongoHttpFact]
    public async Task PersonalListAndManagementPageUseDifferentScopes()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var adminSession = await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var adminId = adminSession["user"]!["id"]!.GetValue<string>();
        var viewer = await fixture.CreateUserAsync("clip-list@example.test");
        const string avatarUrl = "https://example.test/avatar.png";
        const string viewerName = "03 Viewer";
        var users = fixture.Context.Collection<UserAccount>("users");
        await users.UpdateOneAsync(user => user.Id == viewer.Id,
            Builders<UserAccount>.Update.Set(user => user.AvatarUrl, avatarUrl).Set(user => user.DisplayName, viewerName));
        await users.UpdateOneAsync(user => user.Id == adminId,
            Builders<UserAccount>.Update.Set(user => user.DisplayName, "01 Admin"));
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);

        var now = DateTime.UtcNow;
        var owned = new Clip { Id = "a", OwnerId = viewer.Id, CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(1), StartMs = 9000, EndMs = 10000 };
        var admin = new Clip { Id = "b", OwnerId = adminId, CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(1), StartMs = 2000, EndMs = 9000 };
        var orphan = new Clip { Id = "c", OwnerId = "02-missing-owner", CreatedAtUtc = now.AddDays(-1), ExpiresAtUtc = now.AddDays(1), EndMs = 5000 };
        var expired = new Clip { OwnerId = viewer.Id, ExpiresAtUtc = now.AddDays(-1) };
        var trashed = new Clip { OwnerId = adminId, IsTrashed = true, ExpiresAtUtc = now.AddDays(1) };
        var pending = new Clip { OwnerId = adminId, PublicationPending = true, ExpiresAtUtc = now.AddDays(1) };
        await fixture.Context.Collection<Clip>("clips").InsertManyAsync([owned, admin, orphan, expired, trashed, pending]);

        Assert.Equal(admin.Id, Assert.Single((await fixture.Admin.Client.GetFromJsonAsync<Clip[]>("/api/clips"))!).Id);
        Assert.Equal(owned.Id, Assert.Single((await browser.Client.GetFromJsonAsync<Clip[]>("/api/clips"))!).Id);
        await AssertStatusAsync(HttpStatusCode.Forbidden, browser.Client.GetAsync("/api/admin/clips"));

        await SetPermissionsAsync(fixture, viewer.Id, PermissionIds.MANAGE_CLIPS);
        var first = (await browser.Client.GetFromJsonAsync<ManagedClipPage>("/api/admin/clips?pageSize=2"))!;
        Assert.Equal(3, first.Total);
        Assert.Equal(new[] { admin.Id, owned.Id }, first.Items.Select(item => item.Id));
        Assert.Equal(viewerName, first.Items[1].OwnerDisplayName);
        Assert.Equal(avatarUrl, first.Items[1].OwnerAvatarUrl);
        var second = (await browser.Client.GetFromJsonAsync<ManagedClipPage>("/api/admin/clips?page=2&pageSize=2"))!;
        var orphanRow = Assert.Single(second.Items);
        Assert.Equal(orphan.OwnerId, orphanRow.OwnerDisplayName);
        Assert.Null(orphanRow.OwnerAvatarUrl);
        var ascending = (await browser.Client.GetFromJsonAsync<ManagedClipPage>("/api/admin/clips?direction=asc"))!;
        Assert.Equal(new[] { orphan.Id, admin.Id, owned.Id }, ascending.Items.Select(item => item.Id));
        var owners = (await browser.Client.GetFromJsonAsync<ManagedClipPage>("/api/admin/clips?sort=ownerDisplayName&direction=asc&pageSize=2"))!;
        Assert.Equal(new[] { admin.Id, orphan.Id }, owners.Items.Select(item => item.Id));
        var remainingOwner = (await browser.Client.GetFromJsonAsync<ManagedClipPage>("/api/admin/clips?sort=ownerDisplayName&direction=asc&pageSize=2&page=2"))!;
        Assert.Equal(owned.Id, Assert.Single(remainingOwner.Items).Id);
        var durations = (await browser.Client.GetFromJsonAsync<ManagedClipPage>("/api/admin/clips?sort=duration&direction=desc&pageSize=2"))!;
        Assert.Equal(new[] { admin.Id, orphan.Id }, durations.Items.Select(item => item.Id));
        await AssertStatusAsync(HttpStatusCode.BadRequest, browser.Client.GetAsync("/api/admin/clips?pageSize=101"));
        await AssertStatusAsync(HttpStatusCode.BadRequest, browser.Client.GetAsync("/api/admin/clips?sort=relativePath"));
        Assert.Equal(owned.Id, Assert.Single((await browser.Client.GetFromJsonAsync<Clip[]>("/api/clips"))!).Id);

        await SetPermissionsAsync(fixture, viewer.Id);
        await AssertStatusAsync(HttpStatusCode.Forbidden, browser.Client.GetAsync("/api/admin/clips"));
    }

    /// <summary>跨账号限于切片权限，回收支持部分成功、重试和恢复原归属。</summary>
    [MongoHttpFact]
    public async Task DelegatedClipManagementChecksMediaAndTrashPermissions()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var owner = await fixture.CreateUserAsync("clip-owner@example.test");
        var manager = await fixture.CreateUserAsync("clip-manager@example.test");
        var ownerBrowser = fixture.NewBrowser();
        var managerBrowser = fixture.NewBrowser();
        await ownerBrowser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await managerBrowser.LoginAsync(manager.Email, HttpServerFixture.USER_PASSWORD);
        var clip = (await HttpMediaFixtureData.CreateAsync(fixture, owner.Id)).Clip;
        var clipPath = $"/api/clips/{clip.Id}";
        var sharePath = clipPath + "/share";
        var firstShare = await CreateShareAsync(ownerBrowser, sharePath);

        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.Client.GetAsync(sharePath));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.SendAsync(HttpMethod.Post, "/api/admin/clips/trash", new { clipIds = new[] { clip.Id } }));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.Client.GetAsync("/api/admin/trash"));
        await SetPermissionsAsync(fixture, manager.Id, PermissionIds.MANAGE_CLIPS);
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.Client.GetAsync(clipPath + "/media"));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.Client.GetAsync(clipPath + "/download"));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.SendAsync(HttpMethod.Put, sharePath));
        Assert.Equal(firstShare, (await managerBrowser.Client.GetFromJsonAsync<ClipShareLinkResponse>(sharePath))!.Token);
        await AssertStatusAsync(HttpStatusCode.NoContent, managerBrowser.SendAsync(HttpMethod.Delete, $"{sharePath}/{firstShare}"));
        await AssertStatusAsync(HttpStatusCode.NotFound, ownerBrowser.Client.GetAsync($"{sharePath}/{firstShare}"));

        await SetPermissionsAsync(fixture, manager.Id, PermissionIds.MANAGE_CLIPS, PermissionIds.VIEW_RECORDINGS);
        await AssertStatusAsync(HttpStatusCode.OK, managerBrowser.Client.GetAsync(clipPath + "/media"));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.Client.GetAsync(clipPath + "/download"));
        var activeShare = await CreateShareAsync(managerBrowser, sharePath);
        await SetPermissionsAsync(fixture, manager.Id, PermissionIds.MANAGE_CLIPS, PermissionIds.VIEW_RECORDINGS, PermissionIds.DOWNLOAD_RECORDINGS);
        await AssertStatusAsync(HttpStatusCode.OK, managerBrowser.Client.GetAsync(clipPath + "/download"));

        await AssertStatusAsync(HttpStatusCode.BadRequest, managerBrowser.SendAsync(HttpMethod.Post, "/api/admin/clips/trash",
            new { clipIds = new[] { clip.Id, " " } }));
        Assert.Equal(clip.Id, Assert.Single((await ownerBrowser.Client.GetFromJsonAsync<Clip[]>("/api/clips"))!).Id);
        var batch = await TrashClipsAsync(managerBrowser, clip.Id, "missing", clip.Id);
        Assert.Equal(2, batch.Length);
        Assert.True(batch[0].Success);
        Assert.False(batch[1].Success);
        Assert.False(string.IsNullOrWhiteSpace(batch[1].Error));
        Assert.True(Assert.Single(await TrashClipsAsync(managerBrowser, clip.Id)).Success);
        await AssertStatusAsync(HttpStatusCode.NotFound, ownerBrowser.Client.GetAsync($"{sharePath}/{activeShare}"));

        var entry = Assert.Single((await ownerBrowser.Client.GetFromJsonAsync<TrashEntry[]>("/api/trash"))!);
        Assert.Equal(owner.Id, entry.OwnerId);
        Assert.Empty((await fixture.Admin.Client.GetFromJsonAsync<TrashEntry[]>("/api/trash"))!);
        await AssertStatusAsync(HttpStatusCode.Forbidden, fixture.Admin.SendAsync(HttpMethod.Post, $"/api/trash/{entry.Id}/restore"));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.SendAsync(HttpMethod.Delete, $"/api/trash/{entry.Id}"));
        var recording = new TrashEntry { Kind = "Session", EntityId = "recording", OwnerId = manager.Id, State = "Active" };
        var segment = new TrashEntry { Kind = "Segment", EntityId = "segment", OwnerId = manager.Id, State = "Active" };
        await fixture.Context.Collection<TrashEntry>("trash").InsertManyAsync([recording, segment]);
        Assert.Empty((await managerBrowser.Client.GetFromJsonAsync<TrashEntry[]>("/api/trash"))!);
        Assert.Equal(entry.Id, Assert.Single((await managerBrowser.Client.GetFromJsonAsync<TrashEntry[]>("/api/admin/trash"))!).Id);
        Assert.Equal(3, (await fixture.Admin.Client.GetFromJsonAsync<TrashEntry[]>("/api/admin/trash"))!.Length);
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.SendAsync(HttpMethod.Post, $"/api/admin/trash/{recording.Id}/restore"));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.SendAsync(HttpMethod.Delete, $"/api/admin/trash/{segment.Id}"));
        await AssertStatusAsync(HttpStatusCode.NoContent, managerBrowser.SendAsync(HttpMethod.Post, $"/api/admin/trash/{entry.Id}/restore"));
        Assert.Equal(clip.Id, Assert.Single((await ownerBrowser.Client.GetFromJsonAsync<Clip[]>("/api/clips"))!).Id);
        Assert.Empty((await managerBrowser.Client.GetFromJsonAsync<Clip[]>("/api/clips"))!);
        Assert.Null((await ownerBrowser.Client.GetFromJsonAsync<ClipShareLinkResponse>(sharePath))!.Token);

        Assert.True(Assert.Single(await TrashClipsAsync(managerBrowser, clip.Id)).Success);
        entry = Assert.Single((await managerBrowser.Client.GetFromJsonAsync<TrashEntry[]>("/api/admin/trash"))!);
        using var purge = await managerBrowser.SendAsync(HttpMethod.Post, "/api/admin/trash/purge",
            new { entryIds = new[] { recording.Id, entry.Id, segment.Id } });
        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        var results = (await purge.Content.ReadFromJsonAsync<TrashPurgeResult[]>())!;
        Assert.Equal(entry.Id, Assert.Single(results, result => result.Success).EntryId);
        Assert.Equal(2, (await fixture.Admin.Client.GetFromJsonAsync<TrashEntry[]>("/api/admin/trash"))!.Length);
        Assert.False(await fixture.Context.Collection<Clip>("clips").Find(item => item.Id == clip.Id).AnyAsync());

        await SetPermissionsAsync(fixture, manager.Id, PermissionIds.VIEW_RECORDINGS);
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.Client.GetAsync("/api/admin/trash"));
        await AssertStatusAsync(HttpStatusCode.Forbidden, managerBrowser.SendAsync(HttpMethod.Post, "/api/admin/clips/trash", new { clipIds = new[] { clip.Id } }));
    }

    private static Task SetPermissionsAsync(HttpServerFixture fixture, string userId, params string[] permissions) =>
        AssertStatusAsync(HttpStatusCode.OK, fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/permissions", permissions));

    private static async Task<string> CreateShareAsync(HttpBrowser browser, string sharePath)
    {
        using var response = await browser.SendAsync(HttpMethod.Put, sharePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClipShareLinkResponse>())!.Token!;
    }

    private static async Task<ClipTrashResult[]> TrashClipsAsync(HttpBrowser browser, params string[] clipIds)
    {
        using var response = await browser.SendAsync(HttpMethod.Post, "/api/admin/clips/trash", new { clipIds });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClipTrashResult[]>())!;
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> request)
    {
        using var response = await request;
        Assert.Equal(expected, response.StatusCode);
    }
}
