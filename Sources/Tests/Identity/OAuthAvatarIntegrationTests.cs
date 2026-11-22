using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Auth;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证 OAuth 头像随身份流程持久化，并保留最后一张可用图片。</summary>
public sealed class OAuthAvatarIntegrationTests
{
    [MongoFact]
    public async Task RegistrationAndLoginCacheProviderAvatarAndKeepLastSuccessfulDownload()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var settings = await fixture.Settings.GetAsync();
        settings.RegistrationEnabled = true;
        settings.RegistrationInvitationRequired = false;
        settings.RegistrationEmailVerificationRequired = false;
        settings.RegistrationApprovalRequired = false;
        settings.OAuthProviders.Add(new()
        {
            Id = "avatar-test",
            Enabled = true,
            ClientId = "test-client",
            AuthorizationEndpoint = "https://identity.example.test/authorize",
            TokenEndpoint = "https://identity.example.test/token",
            UserInfoEndpoint = "https://identity.example.test/userinfo",
            AvatarField = "profile.picture",
        });
        await fixture.Settings.SaveAsync(settings);

        const string PROVIDER_AVATAR = "https://avatars.example.test/user.gif";
        var original = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        var replacement = (byte[])original.Clone();
        replacement[13] = 0xff;
        var avatarResponse = new ByteArrayContent(original);
        var avatarStatus = HttpStatusCode.OK;
        var avatarDownloads = 0;
        fixture.Http.Handle = request =>
        {
            if (request.RequestUri!.AbsoluteUri == PROVIDER_AVATAR)
            {
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                avatarDownloads++;
                return Task.FromResult(new HttpResponseMessage(avatarStatus) { Content = avatarResponse });
            }
            var content = request.RequestUri.AbsolutePath == "/token"
                ? JsonContent.Create(new { access_token = "test-token" })
                : JsonContent.Create(new { sub = "avatar-subject", profile = new { picture = PROVIDER_AVATAR } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };

        var registration = await ExchangeAsync(fixture);
        Assert.NotNull(registration.RegistrationTicket);
        Assert.Equal(0, avatarDownloads);
        var created = await fixture.Identity.CompleteOAuthRegistrationAsync(registration.RegistrationTicket, new("avatar@example.test", "头像测试", "", ""));
        var url = $"/api/auth/avatars/{created.Id}";
        Assert.Equal(url, created.AvatarUrl);
        Assert.Equal(url, (await fixture.Identity.GetUserAsync(created.Id))!.AvatarUrl);

        var avatars = fixture.Context.Collection<BsonDocument>("oauth_avatars");
        var cached = await avatars.Find(new BsonDocument("_id", created.Id)).SingleAsync();
        Assert.Equal("image/gif", cached["ContentType"].AsString);
        Assert.Equal(original, cached["Content"].AsBsonBinaryData.Bytes);
        var originalEtag = cached["ETag"].AsString;

        avatarResponse = new ByteArrayContent(replacement);
        var refreshed = await ExchangeAsync(fixture);
        Assert.Equal(url, refreshed.User!.AvatarUrl);
        cached = await avatars.Find(new BsonDocument("_id", created.Id)).SingleAsync();
        Assert.Equal(replacement, cached["Content"].AsBsonBinaryData.Bytes);
        Assert.NotEqual(originalEtag, cached["ETag"].AsString);

        avatarStatus = HttpStatusCode.ServiceUnavailable;
        avatarResponse = new StringContent("unavailable");
        var offline = await ExchangeAsync(fixture);
        Assert.Equal(url, offline.User!.AvatarUrl);
        avatarStatus = HttpStatusCode.OK;
        avatarResponse = new StringContent("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var unsupported = await ExchangeAsync(fixture);
        Assert.Equal(url, unsupported.User!.AvatarUrl);
        Assert.Equal(cached, await avatars.Find(new BsonDocument("_id", created.Id)).SingleAsync());
        Assert.Equal(4, avatarDownloads);
    }

    private static async Task<OAuthLoginResult> ExchangeAsync(IdentityFixture fixture)
    {
        var authorization = await fixture.Identity.BeginOAuthAsync("avatar-test", "https://recorder.example.test/oauth/callback/avatar-test", "avatar-browser");
        var state = new Uri(authorization.AuthorizationUrl).Query.TrimStart('?').Split('&')
            .Single(value => value.StartsWith("state=", StringComparison.Ordinal))[6..];
        return await fixture.Identity.CompleteOAuthAsync("avatar-test", Uri.UnescapeDataString(state), "authorization-code", "avatar-browser");
    }
}
