using System.Net;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证媒体权限即时撤销时所有资源地址和任务重试均无法绕过。</summary>
public sealed class RecordingPermissionsHttpTests
{
    /// <summary>撤销观看权限立即封锁已生成清单、媒体分片、封面和所有场次读取入口。</summary>
    [MongoHttpFact]
    public async Task RevokingViewingBlocksExistingResourceUrlsButPreservesPrivateCleanup()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("revoke-viewing@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, user.Id);
        using (var initial = await browser.Client.GetAsync(media.PlaybackUrl))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        }

        await SetPermissionsAsync(fixture, user.Id,
            new(PermissionSet.MemberDefaults.Where(id => id != PermissionIds.VIEW_RECORDINGS)));
        var session = $"/api/sessions/{media.Session.Id}";
        foreach (var path in new[]
                 {
                     "/api/sessions", "/api/streamers", session, session + "/cover", session + "/events",
                     session + "/timeline", session + "/bookmarks", session + "/events/1/avatar",
                     session + "/events/1/emoticons/0", media.PlaybackUrl,
                     media.PlaybackUrl.Replace("index.m3u8", "segment0.ts", StringComparison.Ordinal),
                     $"/api/clips/{media.Clip.Id}/media", $"/api/clips/{media.Clip.Id}/download",
                     $"/api/jobs/{media.Job.Id}/outputs/0"
                 })
        {
            using var response = await browser.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var playback = await browser.SendAsync(HttpMethod.Post, session + "/playback", new { offsetMs = 0 }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, playback.StatusCode);
        }

        using (var cancel = await browser.SendAsync(HttpMethod.Post, $"/api/jobs/{media.Job.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        using var cleanup = await browser.SendAsync(HttpMethod.Delete, $"/api/clips/{media.Clip.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cleanup.StatusCode);
    }

    /// <summary>下载权限对原始文件和所有产物别名一致，撤销后仍可观看。</summary>
    [MongoHttpFact]
    public async Task RevokingDownloadBlocksEveryAttachmentAliasWhileViewingStillWorks()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("revoke-download@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, user.Id);
        await SetPermissionsAsync(fixture, user.Id,
            new(PermissionSet.MemberDefaults.Where(id => id != PermissionIds.DOWNLOAD_RECORDINGS)));
        foreach (var path in new[]
                 {
                     $"/api/sessions/{media.Session.Id}/segments/known-id/download",
                     $"/api/clips/{media.Clip.Id}/download", $"/api/jobs/{media.Job.Id}/outputs/0"
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Range = new(0, 9);
            using var response = await browser.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var preview = await browser.Client.GetAsync($"/api/clips/{media.Clip.Id}/media");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    /// <summary>创建权限撤销后新建与旧任务重试同时受限，既有产物仍按读取权限交付。</summary>
    [MongoHttpFact]
    public async Task RevokingClipCreationAlsoBlocksRetryWithoutRevokingOwnedOutputs()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("revoke-create-clip@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, user.Id);
        await SetPermissionsAsync(fixture, user.Id,
            new(PermissionSet.MemberDefaults.Where(id => id != PermissionIds.CREATE_CLIPS)));
        using (var created = await browser.SendAsync(HttpMethod.Post, "/api/jobs",
                   new { kind = "Clip", sessionId = media.Session.Id, mode = "Exact", startMs = 0, endMs = 1000 }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        }

        using (var output = await browser.Client.GetAsync($"/api/jobs/{media.Job.Id}/outputs/0"))
        {
            Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        }

        await fixture.Context.Collection<MediaJob>("jobs").UpdateOneAsync(job => job.Id == media.Job.Id,
            Builders<MediaJob>.Update.Set(job => job.State, "Failed"));
        using var retried = await browser.SendAsync(HttpMethod.Post, $"/api/jobs/{media.Job.Id}/retry");
        Assert.Equal(HttpStatusCode.Forbidden, retried.StatusCode);
        Assert.Equal("Failed",
            (await fixture.Context.Collection<MediaJob>("jobs").Find(job => job.Id == media.Job.Id).SingleAsync())
            .State);
    }

    private static async Task SetPermissionsAsync(HttpServerFixture fixture, string userId, PermissionSet permissions)
    {
        using var response =
            await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/permissions", permissions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
