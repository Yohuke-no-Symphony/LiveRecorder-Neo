using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证私密分享的权限、提取凭证和链接生命周期。</summary>
public sealed class PrivateClipShareHttpTests
{
    private const string FIRST_PASSWORD = "first-share-password";
    private const string NEXT_PASSWORD = "changed-share-password";
    private const string PASSWORD_REQUIRED = "LIBRARY_CLIP_SHARE_PASSWORD_REQUIRED";
    private const string PASSWORD_INVALID = "LIBRARY_CLIP_SHARE_PASSWORD_INVALID";

    [MongoHttpFact]
    public async Task PrivateShareProtectsCardAndRangeAndBindsItsGrantToCurrentAccount()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var owner = await fixture.CreateUserAsync("private-share-owner@example.test");
        var recipient = await fixture.CreateUserAsync("private-share-recipient@example.test");
        var stranger = await fixture.CreateUserAsync("private-share-stranger@example.test");
        var ownerBrowser = fixture.NewBrowser();
        var recipientBrowser = fixture.NewBrowser();
        var otherSession = fixture.NewBrowser();
        var strangerBrowser = fixture.NewBrowser();
        await ownerBrowser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await recipientBrowser.LoginAsync(recipient.Email, HttpServerFixture.USER_PASSWORD);
        await otherSession.LoginAsync(recipient.Email, HttpServerFixture.USER_PASSWORD);
        await strangerBrowser.LoginAsync(stranger.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        var share = $"/api/clips/{media.Clip.Id}/share";
        var token = await SaveShareAsync(ownerBrowser, share, null, "private", FIRST_PASSWORD);
        var card = share + "/" + token;

        var management = await ownerBrowser.Client.GetFromJsonAsync<JsonObject>(share);
        Assert.NotNull(management);
        Assert.Equal(new[] { "token", "visibility" }, management.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(token, management["token"]!.GetValue<string>());
        Assert.Equal("private", management["visibility"]!.GetValue<string>());
        var stored = await StoredClipAsync(fixture, media.Clip.Id);
        Assert.Equal("private", stored["ShareVisibility"].AsString);
        Assert.NotEqual(FIRST_PASSWORD, stored["SharePasswordHash"].AsString);
        await AssertShareStatusAsync(fixture.NewBrowser(), card, HttpStatusCode.Unauthorized);
        await AssertShareStatusAsync(recipientBrowser, card, HttpStatusCode.Forbidden, PASSWORD_REQUIRED);
        using (var direct = await recipientBrowser.Client.GetAsync(card + "?pwd=" + Uri.EscapeDataString(FIRST_PASSWORD)))
        {
            await AssertProblemAsync(direct, HttpStatusCode.Forbidden, PASSWORD_REQUIRED);
        }
        using (var missingCsrf = await recipientBrowser.SendAsync(HttpMethod.Post, card + "/unlock", new { password = FIRST_PASSWORD }, includeCsrf: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        }
        using (var wrong = await recipientBrowser.SendAsync(HttpMethod.Post, card + "/unlock", new { password = "wrong-password" }))
        {
            await AssertProblemAsync(wrong, HttpStatusCode.Forbidden, PASSWORD_INVALID);
        }
        var grant = await UnlockAsync(recipientBrowser, card, FIRST_PASSWORD);
        Assert.True(grant.HttpOnly);
        Assert.Equal(card, grant.Path.ToString());
        using (var unlocked = await recipientBrowser.Client.GetAsync(card))
        {
            Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
            Assert.True(unlocked.Headers.CacheControl?.NoStore);
            var payload = await unlocked.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(media.Clip.Title, payload!["title"]!.GetValue<string>());
        }
        using (var cover = await recipientBrowser.Client.GetAsync(card + "/cover"))
        {
            Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
            Assert.True(cover.Headers.CacheControl?.NoStore);
            Assert.Equal(media.ImageBytes, await cover.Content.ReadAsByteArrayAsync());
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, card + "/download"))
        {
            request.Headers.Range = new RangeHeaderValue(10, 19);
            using var range = await recipientBrowser.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal("bytes 10-19/64", range.Content.Headers.ContentRange!.ToString());
            Assert.Equal(media.ClipBytes[10..20], await range.Content.ReadAsByteArrayAsync());
        }

        await AssertShareStatusAsync(otherSession, card, HttpStatusCode.Forbidden, PASSWORD_REQUIRED);
        CopyGrant(fixture, strangerBrowser, grant);
        await AssertShareStatusAsync(strangerBrowser, card, HttpStatusCode.Forbidden, PASSWORD_REQUIRED);
        var users = fixture.Context.Collection<UserAccount>("users");
        await users.UpdateOneAsync(user => user.Id == recipient.Id, Builders<UserAccount>.Update.Inc(user => user.SessionVersion, 1));
        await AssertShareStatusAsync(recipientBrowser, card, HttpStatusCode.Unauthorized);
        await recipientBrowser.LoginAsync(recipient.Email, HttpServerFixture.USER_PASSWORD);
        CopyGrant(fixture, recipientBrowser, grant);
        await AssertShareStatusAsync(recipientBrowser, card, HttpStatusCode.Forbidden, PASSWORD_REQUIRED);
    }

    [MongoHttpFact]
    public async Task ChangingShareSettingsRotatesTheTokenAndStaleWritesCannotReplaceIt()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var owner = await fixture.CreateUserAsync("rotate-share-owner@example.test");
        var viewer = await fixture.CreateUserAsync("rotate-share-viewer@example.test");
        var ownerBrowser = fixture.NewBrowser();
        var viewerBrowser = fixture.NewBrowser();
        await ownerBrowser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        await viewerBrowser.LoginAsync(viewer.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        var share = $"/api/clips/{media.Clip.Id}/share";
        using (var invalid = await viewerBrowser.SendAsync(HttpMethod.Put, share + "/settings", new { token = (string?)null, visibility = "private", password = FIRST_PASSWORD }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
        }
        using (var missingPassword = await ownerBrowser.SendAsync(HttpMethod.Put, share + "/settings", new { token = (string?)null, visibility = "private" }))
        {
            await AssertProblemAsync(missingPassword, HttpStatusCode.BadRequest, "LIBRARY_CLIP_SHARE_SETTINGS_INVALID");
        }
        var initial = await SaveShareAsync(ownerBrowser, share, null, "private", FIRST_PASSWORD);
        var initialCard = share + "/" + initial;
        await UnlockAsync(viewerBrowser, initialCard, FIRST_PASSWORD);
        Assert.Equal(initial, await SaveShareAsync(ownerBrowser, share, initial, "private"));

        var rotated = await SaveShareAsync(ownerBrowser, share, initial, "private", NEXT_PASSWORD);
        Assert.NotEqual(initial, rotated);
        var rotatedCard = share + "/" + rotated;
        await AssertShareStatusAsync(viewerBrowser, initialCard, HttpStatusCode.NotFound);
        await AssertShareStatusAsync(viewerBrowser, rotatedCard, HttpStatusCode.Forbidden, PASSWORD_REQUIRED);
        using (var superseded = await viewerBrowser.SendAsync(HttpMethod.Post, rotatedCard + "/unlock", new { password = FIRST_PASSWORD }))
        {
            await AssertProblemAsync(superseded, HttpStatusCode.Forbidden, PASSWORD_INVALID);
        }
        await UnlockAsync(viewerBrowser, rotatedCard, NEXT_PASSWORD);
        using (var stale = await ownerBrowser.SendAsync(HttpMethod.Put, share + "/settings", new { token = initial, visibility = "public" }))
        {
            await AssertProblemAsync(stale, HttpStatusCode.Conflict, "LIBRARY_CLIP_SHARE_SETTINGS_CONFLICT");
        }
        await AssertShareStatusAsync(viewerBrowser, rotatedCard, HttpStatusCode.OK);

        var published = await SaveShareAsync(ownerBrowser, share, rotated, "public");
        Assert.NotEqual(rotated, published);
        var publicCard = share + "/" + published;
        await AssertShareStatusAsync(viewerBrowser, rotatedCard, HttpStatusCode.NotFound);
        await AssertShareStatusAsync(viewerBrowser, publicCard, HttpStatusCode.OK);
        Assert.False((await StoredClipAsync(fixture, media.Clip.Id)).Contains("SharePasswordHash"));
        using (var staleRevoke = await ownerBrowser.SendAsync(HttpMethod.Delete, rotatedCard))
        {
            Assert.Equal(HttpStatusCode.NoContent, staleRevoke.StatusCode);
        }
        await AssertShareStatusAsync(viewerBrowser, publicCard, HttpStatusCode.OK);
        using (var revoked = await ownerBrowser.SendAsync(HttpMethod.Delete, publicCard))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }
        await AssertShareStatusAsync(viewerBrowser, publicCard, HttpStatusCode.NotFound);
        AssertShareFieldsCleared(await StoredClipAsync(fixture, media.Clip.Id));
    }

    [MongoHttpFact]
    public async Task TrashingAndRestoringPrivateClipClearsAllShareSettings()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var owner = await fixture.CreateUserAsync("trash-private-share@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(owner.Email, HttpServerFixture.USER_PASSWORD);
        var media = await HttpMediaFixtureData.CreateAsync(fixture, owner.Id);
        var clip = $"/api/clips/{media.Clip.Id}";
        var share = clip + "/share";
        var token = await SaveShareAsync(browser, share, null, "private", FIRST_PASSWORD);
        var card = share + "/" + token;
        await UnlockAsync(browser, card, FIRST_PASSWORD);

        using var trashed = await browser.SendAsync(HttpMethod.Delete, clip);
        Assert.Equal(HttpStatusCode.NoContent, trashed.StatusCode);
        AssertShareFieldsCleared(await StoredClipAsync(fixture, media.Clip.Id));
        await AssertShareStatusAsync(browser, card, HttpStatusCode.NotFound);
        var entries = await browser.Client.GetFromJsonAsync<TrashEntry[]>("/api/trash");
        var entry = Assert.Single(entries!, candidate => candidate.EntityId == media.Clip.Id);
        using var restored = await browser.SendAsync(HttpMethod.Post, $"/api/trash/{entry.Id}/restore");
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        var current = await browser.Client.GetFromJsonAsync<JsonObject>(share);
        Assert.Null(current!["token"]);
        Assert.Null(current["visibility"]);
        await AssertShareStatusAsync(browser, card, HttpStatusCode.NotFound);
        AssertShareFieldsCleared(await StoredClipAsync(fixture, media.Clip.Id));
    }

    private static async Task<string> SaveShareAsync(HttpBrowser browser, string share, string? token, string visibility, string? password = null)
    {
        using var response = await browser.SendAsync(HttpMethod.Put, share + "/settings", new { token, visibility, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(visibility, settings!["visibility"]!.GetValue<string>());
        return settings["token"]!.GetValue<string>();
    }

    private static async Task<SetCookieHeaderValue> UnlockAsync(HttpBrowser browser, string card, string password)
    {
        using var response = await browser.SendAsync(HttpMethod.Post, card + "/unlock", new { password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie").Select(value => SetCookieHeaderValue.Parse(value)),
            cookie => cookie.Name.ToString() == "lr.clip-share");
    }

    private static void CopyGrant(HttpServerFixture fixture, HttpBrowser browser, SetCookieHeaderValue grant) =>
        browser.Cookies.Add(fixture.Address, new Cookie(grant.Name.ToString(), grant.Value.ToString(), grant.Path.ToString()));

    private static async Task AssertShareStatusAsync(HttpBrowser browser, string card, HttpStatusCode status, string? code = null)
    {
        foreach (var suffix in new[] { "", "/cover", "/download" })
        {
            using var response = await browser.Client.GetAsync(card + suffix);
            if (code is null)
            {
                Assert.Equal(status, response.StatusCode);
            }
            else
            {
                await AssertProblemAsync(response, status, code);
            }
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(code, problem!["code"]!.GetValue<string>());
    }

    private static Task<BsonDocument> StoredClipAsync(HttpServerFixture fixture, string id) => fixture.Context.Collection<BsonDocument>("clips")
        .Find(new BsonDocument("_id", id)).SingleAsync();

    private static void AssertShareFieldsCleared(BsonDocument clip)
    {
        Assert.False(clip.Contains("ShareToken"));
        Assert.False(clip.Contains("ShareVisibility"));
        Assert.False(clip.Contains("SharePasswordHash"));
    }
}
