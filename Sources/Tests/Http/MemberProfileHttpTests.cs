using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证站内个人主页字段、列表权限和公开分享分页。</summary>
public sealed class MemberProfileHttpTests
{
    [MongoHttpFact]
    public async Task ProfileExposesOnlyActiveMemberIdentityAndListRequiresRecordingPermission()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var owner = await fixture.CreateUserAsync("profile-owner@example.test");
        var viewer = await fixture.CreateUserAsync("profile-viewer@example.test");
        const string AVATAR = "/api/auth/avatars/old-profile-image";
        var users = fixture.Context.Collection<UserAccount>("users");
        await users.UpdateOneAsync(user => user.Id == owner.Id, Builders<UserAccount>.Update.Set(user => user.AvatarUrl, AVATAR));
        await users.UpdateOneAsync(user => user.Id == viewer.Id, Builders<UserAccount>.Update.Set(user => user.Permissions, PermissionSet.Empty));
        var profile = $"/api/users/{owner.Id}";
        var anonymous = fixture.NewBrowser();
        await AssertStatusAsync(anonymous, profile, HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, profile + "/shares", HttpStatusCode.Unauthorized);

        var browser = fixture.NewBrowser();
        await browser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);
        var identity = await browser.Client.GetFromJsonAsync<JsonObject>(profile);
        Assert.NotNull(identity);
        Assert.Equal(new[] { "avatarUrl", "displayName", "id" }, identity.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(owner.Id, identity["id"]!.GetValue<string>());
        Assert.Equal(owner.DisplayName, identity["displayName"]!.GetValue<string>());
        Assert.Equal(AVATAR, identity["avatarUrl"]!.GetValue<string>());
        await AssertStatusAsync(browser, profile + "/shares", HttpStatusCode.Forbidden);

        await users.UpdateOneAsync(user => user.Id == viewer.Id, Builders<UserAccount>.Update.Set(user => user.Permissions,
            new PermissionSet([PermissionIds.VIEW_RECORDINGS])));
        var empty = await browser.Client.GetFromJsonAsync<JsonObject>(profile + "/shares");
        Assert.NotNull(empty);
        Assert.Empty(Assert.IsType<JsonArray>(empty["items"]));
        Assert.Null(empty["nextCursor"]);

        await users.UpdateOneAsync(user => user.Id == owner.Id, Builders<UserAccount>.Update.Set(user => user.Status, "Disabled"));
        await AssertStatusAsync(browser, profile, HttpStatusCode.NotFound);
        await AssertStatusAsync(browser, profile + "/shares", HttpStatusCode.NotFound);
    }

    [MongoHttpFact]
    public async Task ProfilePagesExplicitAndLegacyPublicSharesAndExcludesOtherClips()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<StorageMaintenanceWorker>().Single().StopAsync(CancellationToken.None);
        var owner = await fixture.CreateUserAsync("share-profile-owner@example.test");
        var viewer = await fixture.CreateUserAsync("share-profile-viewer@example.test");
        var ownerBrowser = fixture.NewBrowser();
        var viewerBrowser = fixture.NewBrowser();
        await ownerBrowser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await viewerBrowser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        var created = DateTime.UtcNow.AddMinutes(-5);
        var published = CreateSharedClip(media.Clip, "published-share", created);
        var legacy = CreateSharedClip(media.Clip, "legacy-share", created);
        published.ShareToken = null;
        published.ShareVisibility = null;

        var newer = created.AddSeconds(1);
        var privateClip = CreateSharedClip(media.Clip, "private-share", newer);
        privateClip.ShareVisibility = "private";
        privateClip.SharePasswordHash = "stored-private-password-hash";
        var trashed = CreateSharedClip(media.Clip, "trashed-share", newer);
        trashed.IsTrashed = true;
        var expired = CreateSharedClip(media.Clip, "expired-share", newer);
        expired.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        var publishing = CreateSharedClip(media.Clip, "publishing-share", newer);
        publishing.PublicationPending = true;
        var missingFile = CreateSharedClip(media.Clip, "missing-share", newer);
        missingFile.RelativePath = Path.Combine("missing-shares", "not-found.mp4");
        var anotherOwner = CreateSharedClip(media.Clip, "another-owner-share", newer);
        anotherOwner.OwnerId = viewer.Id;
        await fixture.Context.Collection<Clip>("clips").InsertManyAsync([published, privateClip, trashed, expired, publishing, missingFile, anotherOwner]);
        var oldDocument = legacy.ToBsonDocument();
        oldDocument.Remove("ShareVisibility");
        oldDocument.Remove("PublicationPending");
        await fixture.Context.Collection<BsonDocument>("clips").InsertOneAsync(oldDocument);

        using var shareCreate = await ownerBrowser.SendAsync(HttpMethod.Put, $"/api/clips/{published.Id}/share/settings", new { token = (string?)null, visibility = "public" });
        Assert.Equal(HttpStatusCode.OK, shareCreate.StatusCode);
        var share = await shareCreate.Content.ReadFromJsonAsync<JsonObject>();
        var token = share!["token"]!.GetValue<string>();
        var list = $"/api/users/{owner.Id}/shares?limit=1";
        var first = await viewerBrowser.Client.GetFromJsonAsync<JsonObject>(list);
        Assert.NotNull(first);
        AssertItem(Assert.Single(Assert.IsType<JsonArray>(first["items"])), published, token);
        var cursor = Assert.IsType<JsonObject>(first["nextCursor"]);
        var beforeId = cursor["beforeId"]!.GetValue<string>();
        var beforeUtc = cursor["beforeUtc"]!.GetValue<string>();
        Assert.Equal(published.Id, beforeId);

        var second = await viewerBrowser.Client.GetFromJsonAsync<JsonObject>(list + $"&beforeUtc={Uri.EscapeDataString(beforeUtc)}&beforeId={Uri.EscapeDataString(beforeId)}");
        Assert.NotNull(second);
        AssertItem(Assert.Single(Assert.IsType<JsonArray>(second["items"])), legacy, legacy.ShareToken!);
        Assert.Null(second["nextCursor"]);
    }

    private static Clip CreateSharedClip(Clip media, string id, DateTime created) => new()
    {
        Id = id, OwnerId = media.OwnerId, SessionId = media.SessionId, RelativePath = media.RelativePath,
        Title = id, CreatedAtUtc = created, ExpiresAtUtc = media.ExpiresAtUtc,
        Bytes = media.Bytes, StartMs = 125.25, EndMs = 1125.75, ShareToken = id + "-token", ShareVisibility = "public"
    };

    private static void AssertItem(JsonNode? item, Clip clip, string token)
    {
        var info = Assert.IsType<JsonObject>(item);
        Assert.Equal(new[] { "bytes", "createdAtUtc", "durationMs", "expiresAtUtc", "id", "title", "token" }, info.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(clip.Id, info["id"]!.GetValue<string>());
        Assert.Equal(token, info["token"]!.GetValue<string>());
        Assert.Equal(clip.Title, info["title"]!.GetValue<string>());
        Assert.Equal(clip.Bytes, info["bytes"]!.GetValue<long>());
        Assert.Equal(clip.EndMs - clip.StartMs, info["durationMs"]!.GetValue<double>());
    }

    private static async Task AssertStatusAsync(HttpBrowser browser, string path, HttpStatusCode status)
    {
        using var response = await browser.Client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
    }
}
