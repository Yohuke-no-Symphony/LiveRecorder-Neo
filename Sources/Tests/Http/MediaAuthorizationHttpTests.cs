using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Storage;
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
    public async Task SharedClipCardAndDownloadRequireTheirRespectivePermissions()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var owner = await fixture.CreateUserAsync("share-owner@example.test");
        var recipient = await fixture.CreateUserAsync("share-recipient@example.test");
        var users = fixture.Context.Collection<UserAccount>("users");
        await users.UpdateOneAsync(x => x.Id == owner.Id, Builders<UserAccount>.Update.Set(x => x.Permissions,
            new PermissionSet([PermissionIds.VIEW_RECORDINGS])));
        var ownerBrowser = fixture.NewBrowser();
        var recipientBrowser = fixture.NewBrowser();
        await ownerBrowser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await recipientBrowser.LoginAsync(recipient.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        await fixture.Context.Collection<Clip>("clips").UpdateOneAsync(x => x.Id == media.Clip.Id,
            Builders<Clip>.Update.Unset(x => x.PublicationPending).Set(x => x.StartMs, 13500.25).Set(x => x.EndMs, 16500.75));
        var shareUrl = $"/api/clips/{media.Clip.Id}/share";
        using (var missingCsrf = await ownerBrowser.SendAsync(HttpMethod.Put, shareUrl, includeCsrf: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        }

        var token = await CreateShareAsync(ownerBrowser, shareUrl);
        var cardUrl = shareUrl + "/" + Uri.EscapeDataString(token);
        var ownerClips = await ownerBrowser.Client.GetFromJsonAsync<JsonArray>("/api/clips");
        Assert.False(Assert.IsType<JsonObject>(Assert.Single(ownerClips!)).ContainsKey("shareToken"));
        using (var anonymous = await fixture.Admin.Client.GetAsync(shareUrl))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        await AssertSharedEndpointsStatusAsync(fixture.Admin, cardUrl, HttpStatusCode.Unauthorized);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var denied = await recipientBrowser.SendAsync(method, shareUrl);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var denied = await recipientBrowser.SendAsync(HttpMethod.Delete, cardUrl))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var response = await recipientBrowser.Client.GetAsync(cardUrl))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var card = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(card);
            Assert.Equal(new[] { "bytes", "durationMs", "expiresAtUtc", "title" }, card.Select(item => item.Key).Order(StringComparer.Ordinal));
            Assert.Equal(media.Clip.Title, card["title"]!.GetValue<string>());
            Assert.Equal(media.Clip.Bytes, card["bytes"]!.GetValue<long>());
            Assert.Equal(3000.5, card["durationMs"]!.GetValue<double>());
            Assert.Equal(new DateTimeOffset(media.Clip.ExpiresAtUtc).ToUnixTimeMilliseconds(),
                card["expiresAtUtc"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());
        }
        using (var denied = await ownerBrowser.Client.GetAsync(cardUrl + "/download"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var response = await recipientBrowser.Client.GetAsync(cardUrl + "/download"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(media.ClipBytes, await response.Content.ReadAsByteArrayAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, cardUrl + "/download"))
        {
            request.Headers.Range = new RangeHeaderValue(10, 19);
            using var ranged = await recipientBrowser.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal("bytes 10-19/64", ranged.Content.Headers.ContentRange!.ToString());
            Assert.Equal(media.ClipBytes[10..20], await ranged.Content.ReadAsByteArrayAsync());
        }
        foreach (var path in new[] { $"/api/clips/{media.Clip.Id}/download", $"/api/clips/{media.Clip.Id}/media", $"/api/jobs/{media.Job.Id}/outputs/0" })
        {
            using var denied = await recipientBrowser.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await users.UpdateOneAsync(x => x.Id == recipient.Id, Builders<UserAccount>.Update.Set(x => x.Permissions,
            new PermissionSet([PermissionIds.DOWNLOAD_RECORDINGS])));
        await AssertSharedEndpointsStatusAsync(recipientBrowser, cardUrl, HttpStatusCode.Forbidden);
        await users.UpdateOneAsync(x => x.Id == recipient.Id, Builders<UserAccount>.Update.Set(x => x.Status, "Disabled"));
        await AssertSharedEndpointsStatusAsync(recipientBrowser, cardUrl, HttpStatusCode.Unauthorized);

        await users.UpdateOneAsync(x => x.Id == owner.Id, Builders<UserAccount>.Update.Set(x => x.Permissions, PermissionSet.Empty));
        var current = await ownerBrowser.Client.GetFromJsonAsync<JsonObject>(shareUrl);
        Assert.Equal(token, current!["token"]!.GetValue<string>());
        using (var denied = await ownerBrowser.SendAsync(HttpMethod.Put, shareUrl))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var revoked = await ownerBrowser.SendAsync(HttpMethod.Delete, cardUrl);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    }

    [MongoHttpFact]
    public async Task ConcurrentShareCreationAndStaleRevocationKeepTheCurrentLink()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var owner = await fixture.CreateUserAsync("share-managed-owner@example.test");
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        var shareUrl = $"/api/clips/{media.Clip.Id}/share";
        var initial = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>(shareUrl);
        Assert.True(initial!.ContainsKey("token"));
        Assert.Null(initial["token"]);

        var tokens = await Task.WhenAll(CreateShareAsync(fixture.Admin, shareUrl), CreateShareAsync(fixture.Admin, shareUrl));
        Assert.Equal(tokens[0], tokens[1]);
        var firstCard = shareUrl + "/" + Uri.EscapeDataString(tokens[0]);
        using (var revoked = await fixture.Admin.SendAsync(HttpMethod.Delete, firstCard))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }
        await AssertSharedEndpointsStatusAsync(fixture.Admin, firstCard, HttpStatusCode.NotFound);

        var replacement = await CreateShareAsync(fixture.Admin, shareUrl);
        Assert.NotEqual(tokens[0], replacement);
        using (var stale = await fixture.Admin.SendAsync(HttpMethod.Delete, firstCard))
        {
            Assert.Equal(HttpStatusCode.NoContent, stale.StatusCode);
        }
        var current = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>(shareUrl);
        Assert.Equal(replacement, current!["token"]!.GetValue<string>());
        await AssertSharedEndpointsStatusAsync(fixture.Admin, firstCard, HttpStatusCode.NotFound);
        await AssertSharedEndpointsStatusAsync(fixture.Admin, shareUrl + "/" + Uri.EscapeDataString(replacement), HttpStatusCode.OK);
    }

    [MongoHttpFact]
    public async Task RestoringTrashedClipDoesNotRestoreItsShareLink()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var owner = await fixture.CreateUserAsync("share-trash-owner@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        var clipUrl = $"/api/clips/{media.Clip.Id}";
        var shareUrl = clipUrl + "/share";
        var token = await CreateShareAsync(browser, shareUrl);
        var cardUrl = shareUrl + "/" + Uri.EscapeDataString(token);
        using (var trashed = await browser.SendAsync(HttpMethod.Delete, clipUrl))
        {
            Assert.Equal(HttpStatusCode.NoContent, trashed.StatusCode);
        }
        await AssertSharedEndpointsStatusAsync(browser, cardUrl, HttpStatusCode.NotFound);

        var trash = await browser.Client.GetFromJsonAsync<TrashEntry[]>("/api/trash");
        var entry = Assert.Single(trash!, item => item.EntityId == media.Clip.Id);
        using (var restored = await browser.SendAsync(HttpMethod.Post, $"/api/trash/{entry.Id}/restore"))
        {
            Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        }
        var current = await browser.Client.GetFromJsonAsync<JsonObject>(shareUrl);
        Assert.Null(current!["token"]);
        await AssertSharedEndpointsStatusAsync(browser, cardUrl, HttpStatusCode.NotFound);
        Assert.NotEqual(token, await CreateShareAsync(browser, shareUrl));
    }

    [MongoHttpFact]
    public async Task ValidShareLinkRequiresAnUnexpiredClipAndAnExistingFile()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<StorageMaintenanceWorker>().Single().StopAsync(CancellationToken.None);
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var admin = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Role == "Admin").SingleAsync();
        var media = await HttpMediaFixtureData.CreateAsync(fixture, admin.Id);
        var shareUrl = $"/api/clips/{media.Clip.Id}/share";
        var token = await CreateShareAsync(fixture.Admin, shareUrl);
        var cardUrl = shareUrl + "/" + Uri.EscapeDataString(token);
        var file = Path.Combine(fixture.StoragePath, media.Clip.RelativePath);
        File.Delete(file);
        await AssertSharedEndpointsStatusAsync(fixture.Admin, cardUrl, HttpStatusCode.Gone);

        await File.WriteAllBytesAsync(file, media.ClipBytes);
        await fixture.Context.Collection<Clip>("clips").UpdateOneAsync(x => x.Id == media.Clip.Id,
            Builders<Clip>.Update.Set(x => x.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        await AssertSharedEndpointsStatusAsync(fixture.Admin, cardUrl, HttpStatusCode.Gone);
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

    private static async Task<string> CreateShareAsync(HttpBrowser browser, string path)
    {
        using var response = await browser.SendAsync(HttpMethod.Put, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var share = await response.Content.ReadFromJsonAsync<JsonObject>();
        var token = Assert.IsType<string>(share?["token"]?.GetValue<string>());
        Assert.NotEmpty(token);
        return token;
    }

    private static async Task AssertSharedEndpointsStatusAsync(HttpBrowser browser, string cardUrl, HttpStatusCode status)
    {
        foreach (var path in new[] { cardUrl, cardUrl + "/download" })
        {
            using var response = await browser.Client.GetAsync(path);
            Assert.Equal(status, response.StatusCode);
        }
    }
}
