using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证原生上传、共享阅读、文件归属和既有登录会话的实时授权。</summary>
public sealed class SessionLiveMarkerHttpTests
{
    /// <summary>普通成员共享文件列表，只有上传者、记录管理员和管理员能校准或删除。</summary>
    [MongoHttpFact]
    public async Task SharedFilesRespectOwnershipIndependentUploadAndLivePermissionChanges()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var author = await fixture.CreateUserAsync("marker-author@example.test");
        var viewer = await fixture.CreateUserAsync("marker-viewer@example.test");
        var authorBrowser = fixture.NewBrowser();
        var viewerBrowser = fixture.NewBrowser();
        await authorBrowser.LoginAsync(author.Email, HttpServerFixture.USER_PASSWORD);
        await viewerBrowser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, author.Id);
        var path = $"/api/sessions/{media.Session.Id}/live-markers";
        LiveMarkerFileSummary file;
        using (var uploaded = await UploadAsync(authorBrowser, path, Yaml(media.Session.StartedAtUtc), "场次记录.yaml"))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            file = (await uploaded.Content.ReadFromJsonAsync<LiveMarkerFileSummary>())!;
            Assert.True(file.CanManage);
            Assert.Equal(author.Id, file.UploaderId);
            Assert.Equal(64, file.Revision.Length);
        }

        var list = await viewerBrowser.Client.GetFromJsonAsync<LiveMarkerFileListResponse>(path);
        Assert.False(Assert.Single(list!.Files).CanManage);
        Assert.Equal(file.Revision, Assert.Single(list.Files).Revision);
        var filePath = path + "/" + file.Id;
        var detail = await viewerBrowser.Client.GetFromJsonAsync<LiveMarkerFileResponse>(filePath);
        Assert.Equal(1500, Assert.Single(detail!.Entries).OffsetMs);
        Assert.Equal(file.Revision, detail.File.Revision);
        using (var denied = await viewerBrowser.SendAsync(HttpMethod.Delete, filePath))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await SetPermissionsAsync(fixture, author.Id, UserPermissionNames.VIEW_RECORDINGS);
        using (var aligned = await authorBrowser.SendAsync(HttpMethod.Put, filePath + "/alignment",
                   new LiveMarkerAlignmentRequest(-500)))
        {
            Assert.Equal(HttpStatusCode.OK, aligned.StatusCode);
        }
        var calibrated = await authorBrowser.Client.GetFromJsonAsync<LiveMarkerFileResponse>(filePath);
        Assert.NotEqual(file.Revision, calibrated!.File.Revision);

        foreach (var invalid in new object[]
                 {
                     new { }, new { calibrationMs = (long?)null }, new { calibrationMs = "invalid" },
                     new { calibrationMs = 0.5 }
                 })
        {
            using var rejected = await authorBrowser.SendAsync(HttpMethod.Put, filePath + "/alignment", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var unchanged = await authorBrowser.Client.GetFromJsonAsync<LiveMarkerFileResponse>(filePath);
            Assert.Equal(-500, unchanged!.File.CalibrationMs);
        }

        using (var noUpload = await UploadAsync(authorBrowser, path, Yaml(media.Session.StartedAtUtc), "记录.yaml"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noUpload.StatusCode);
        }

        await SetPermissionsAsync(fixture, viewer.Id, UserPermissionNames.VIEW_RECORDINGS,
            UserPermissionNames.MANAGE_LIVE_MARKERS);
        using (var aligned = await viewerBrowser.SendAsync(HttpMethod.Put, filePath + "/alignment",
                   new LiveMarkerAlignmentRequest(250)))
        {
            Assert.Equal(HttpStatusCode.OK, aligned.StatusCode);
        }

        await SetPermissionsAsync(fixture, viewer.Id, UserPermissionNames.MANAGE_LIVE_MARKERS);
        using (var noView = await viewerBrowser.Client.GetAsync(filePath))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noView.StatusCode);
        }

        using (var noManageWithoutView = await viewerBrowser.SendAsync(HttpMethod.Delete, filePath))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noManageWithoutView.StatusCode);
        }

        using (var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, filePath))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var missing = await authorBrowser.Client.GetAsync(filePath);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>CSRF、超大文件与畸形 multipart 在附件发布前返回明确的请求错误。</summary>
    [MongoHttpFact]
    public async Task InvalidMultipartAndUploadLimitsNeverPublishPartialFiles()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var profile = await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, profile["user"]!["id"]!.GetValue<string>());
        var path = $"/api/sessions/{media.Session.Id}/live-markers";
        using (var csrf = await UploadAsync(fixture.Admin, path, Yaml(media.Session.StartedAtUtc), "记录.yaml",
                   includeCsrf: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        }

        using (var oversized =
               await UploadAsync(fixture.Admin, path, new byte[LiveMarkerLimits.MAXIMUM_BYTES + 1], "记录.yaml"))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        }

        using (var invalid =
               await UploadAsync(fixture.Admin, path, Encoding.UTF8.GetBytes("not: a document"), "记录.yaml"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, path))
        {
            request.Headers.Add("X-CSRF-TOKEN", fixture.Admin.CsrfToken);
            request.Content = new StringContent("broken multipart");
            request.Content.Headers.ContentType = new("multipart/form-data");
            using var malformed = await fixture.Admin.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        Assert.Empty((await fixture.Admin.Client.GetFromJsonAsync<LiveMarkerFileListResponse>(path))!.Files);
    }

    /// <summary>来源时区在上传时固定，修改站点时区不会重新解释已保存记录。</summary>
    [MongoHttpFact]
    public async Task LegacySourceTimeZoneIsPersistedIndependentlyOfLaterSettings()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var profile = await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, profile["user"]!["id"]!.GetValue<string>());
        var path = $"/api/sessions/{media.Session.Id}/live-markers";
        var yaml = Encoding.UTF8.GetBytes(
            "StartTime: 2026-11-12T00:00:00\nMarker:\n- RealDateTime: 2026-11-12T00:00:01.500\n  LiveTime: 00:00:01.500\n  Message: legacy\n  MarkerColor: Grey\n");
        LiveMarkerFileSummary file;
        using (var uploaded = await UploadAsync(fixture.Admin, path, yaml, "legacy.yml", "Asia/Shanghai"))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            file = (await uploaded.Content.ReadFromJsonAsync<LiveMarkerFileSummary>())!;
        }

        var before = await fixture.Admin.Client.GetFromJsonAsync<LiveMarkerFileResponse>(path + "/" + file.Id);
        var settings = fixture.App.Services.GetRequiredService<ISettingsStore>();
        var current = await settings.GetAsync();
        current.TimeZone = "America/New_York";
        await settings.SaveAsync(current);
        var after = await fixture.Admin.Client.GetFromJsonAsync<LiveMarkerFileResponse>(path + "/" + file.Id);
        Assert.Equal("Asia/Shanghai", after!.File.SourceTimeZoneId);
        Assert.Equal(before!.Entries, after.Entries);
        Assert.Contains("+08:00", after.Entries[0].RealDateTime);
        var original = Path.Combine(fixture.StoragePath, media.Session.DirectoryPath,
            LiveMarkerFileStore.DIRECTORY_NAME, file.Id, LiveMarkerFileStore.ORIGINAL_FILE_NAME);
        Assert.Equal(yaml, await File.ReadAllBytesAsync(original));
    }

    private static byte[] Yaml(DateTime start)
    {
        var storedStart = new DateTime(start.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
        return Encoding.UTF8.GetBytes(
            $"StartTime: {storedStart:O}\nMarker:\n- RealDateTime: {storedStart.AddMilliseconds(1500):O}\n  LiveTime: 00:00:01.500\n  Message: high point\n  MarkerColor: Red\n");
    }

    private static async Task SetPermissionsAsync(HttpServerFixture fixture, string userId, params string[] permissions)
    {
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/permissions",
            new PermissionSet(permissions));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpBrowser browser, string path, byte[] bytes,
        string fileName,
        string timeZoneId = "UTC", bool includeCsrf = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/yaml");
        multipart.Add(file, "file", fileName);
        multipart.Add(new StringContent(timeZoneId), "timeZoneId");
        multipart.Add(new StringContent("0"), "calibrationMs");
        request.Content = multipart;
        if (includeCsrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", browser.CsrfToken);
        }

        return await browser.Client.SendAsync(request);
    }
}
