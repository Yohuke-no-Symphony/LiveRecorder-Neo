using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>真实文件交付的身份边界、私有产物、Range 与过期回归。</summary>
public sealed class MediaAuthorizationHttpTests
{
    [MongoHttpFact]
    public async Task MediaRecordsCannotResolvePrivateConfigurationOrKeysBesideTheRecordingDirectory()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var admin = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Role == "Admin").SingleAsync();
        var media = await HttpMediaFixtureData.CreateAsync(fixture, admin.Id);
        foreach (var relative in new[] { "../settings.json", "../bootstrap.json", "../keys/key.xml" })
        {
            await fixture.Context.Collection<Clip>("clips").UpdateOneAsync(x => x.Id == media.Clip.Id, Builders<Clip>.Update.Set(x => x.RelativePath, relative));
            await fixture.Context.Collection<MediaJob>("jobs").UpdateOneAsync(x => x.Id == media.Job.Id, Builders<MediaJob>.Update.Set(x => x.OutputRelativePaths, [relative]));
            await fixture.Context.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == media.Session.Id, Builders<BroadcastSession>.Update.Set(x => x.CoverRelativePath, relative));
            foreach (var path in new[] { $"/api/clips/{media.Clip.Id}/download", $"/api/jobs/{media.Job.Id}/outputs/0", $"/api/sessions/{media.Session.Id}/cover" })
            {
                using var response = await fixture.Admin.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
                Assert.Equal("PATH_MEDIA_OUTSIDE_STORAGE", problem!["code"]!.GetValue<string>());
            }
        }
    }

    [MongoHttpFact]
    public async Task OnlyClipOwnerOrAdminCanDownloadAndExpiryAlsoBlocksJobOutputAlias()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var owner = await fixture.CreateUserAsync("owner@example.test");
        var other = await fixture.CreateUserAsync("other@example.test");
        var ownerBrowser = fixture.NewBrowser();
        var otherBrowser = fixture.NewBrowser();
        await ownerBrowser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await otherBrowser.LoginAsync(other.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        var clipUrl = $"/api/clips/{media.Clip.Id}/download";
        var mediaUrl = $"/api/clips/{media.Clip.Id}/media";
        var outputUrl = $"/api/jobs/{media.Job.Id}/outputs/0";
        foreach (var path in new[] { clipUrl, mediaUrl, outputUrl, $"/api/jobs/{media.Job.Id}" })
        {
            using var denied = await otherBrowser.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var ownerClips = await ownerBrowser.Client.GetFromJsonAsync<JsonArray>("/api/clips");
        Assert.Single(ownerClips!);
        Assert.Empty((await otherBrowser.Client.GetFromJsonAsync<JsonArray>("/api/clips"))!);
        foreach (var browser in new[] { ownerBrowser, fixture.Admin })
        {
            using var full = await browser.Client.GetAsync(clipUrl);
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal(media.ClipBytes, await full.Content.ReadAsByteArrayAsync());
            Assert.Equal("attachment", full.Content.Headers.ContentDisposition!.DispositionType);
            using var preview = await browser.Client.GetAsync(mediaUrl);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.NotEqual("attachment", preview.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(media.ClipBytes, await preview.Content.ReadAsByteArrayAsync());
        }
        foreach (var path in new[] { clipUrl, mediaUrl })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Range = new RangeHeaderValue(10, 19);
            using var ranged = await ownerBrowser.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal("bytes 10-19/64", ranged.Content.Headers.ContentRange!.ToString());
            Assert.Equal(media.ClipBytes[10..20], await ranged.Content.ReadAsByteArrayAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, clipUrl))
        {
            request.Headers.Range = new RangeHeaderValue(1000, 2000);
            using var invalid = await ownerBrowser.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
            Assert.Equal("bytes */64", invalid.Content.Headers.ContentRange!.ToString());
        }
        await fixture.Context.Collection<Clip>("clips").UpdateOneAsync(x => x.Id == media.Clip.Id, Builders<Clip>.Update.Set(x => x.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        foreach (var browser in new[] { ownerBrowser, fixture.Admin })
        {
            foreach (var path in new[] { clipUrl, mediaUrl, outputUrl })
            {
                using var expired = await browser.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            }
        }
        Assert.Empty((await ownerBrowser.Client.GetFromJsonAsync<JsonArray>("/api/clips"))!);
    }

    [MongoHttpFact]
    public async Task HlsCoverAndEventsRequireActiveMembershipAndPreserveFileResponses()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var user = await fixture.CreateUserAsync("viewer@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, user.Id);
        var cover = $"/api/sessions/{media.Session.Id}/cover";
        var events = $"/api/sessions/{media.Session.Id}/events?fromMs=0&toMs=1000";
        var segment = media.PlaybackUrl.Replace("index.m3u8", "segment0.ts", StringComparison.Ordinal);
        foreach (var path in new[] { media.PlaybackUrl, segment, cover, events })
        {
            using var anonymous = await fixture.Admin.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        using (var response = await browser.Client.GetAsync(media.PlaybackUrl))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/vnd.apple.mpegurl", response.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("#EXTM3U", await response.Content.ReadAsStringAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, segment))
        {
            request.Headers.Range = new RangeHeaderValue(4, 7);
            using var response = await browser.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(media.ClipBytes[4..8], await response.Content.ReadAsByteArrayAsync());
        }
        using (var response = await browser.Client.GetAsync(cover))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(media.ImageBytes, await response.Content.ReadAsByteArrayAsync());
        }
        var page = await browser.Client.GetFromJsonAsync<JsonObject>(events);
        Assert.Equal("测试弹幕", Assert.Single(page!["items"]!.AsArray())!["text"]!.GetValue<string>());
        using (var metadata = await browser.Client.GetAsync(media.PlaybackUrl.Replace("index.m3u8", "window.json", StringComparison.Ordinal)))
        {
            Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        }
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(x => x.Id == user.Id, Builders<UserAccount>.Update.Set(x => x.Status, "Disabled"));
        foreach (var path in new[] { media.PlaybackUrl, segment, cover, events, $"/api/jobs/{media.Job.Id}" })
        {
            using var denied = await browser.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
    }
}
