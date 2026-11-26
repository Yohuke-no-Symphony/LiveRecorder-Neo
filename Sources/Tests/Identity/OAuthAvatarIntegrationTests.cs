using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证 OAuth 注册将头像存为实例文件，后续登录保留该头像。</summary>
public sealed class OAuthAvatarIntegrationTests
{
    [MongoFact]
    public async Task OnlyRegistrationDownloadsTheProviderAvatar()
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
            UserInfoEndpoint = "https://identity.example.test/oauth2/userinfo",
            AvatarField = "profile.picture",
        });
        await fixture.Settings.SaveAsync(settings);

        const string PROVIDER_AVATAR_PATH = "api/user/avatars/user.gif";
        const string PROVIDER_AVATAR_URL = "https://identity.example.test/" + PROVIDER_AVATAR_PATH;
        const string CDN_AVATAR_URL = "https://avatars.example.test/user.gif";
        var original = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        var avatarSource = PROVIDER_AVATAR_PATH;
        var avatarDownloads = new List<string>();
        fixture.Http.Handle = request =>
        {
            if (request.RequestUri!.AbsoluteUri is PROVIDER_AVATAR_URL or CDN_AVATAR_URL)
            {
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                avatarDownloads.Add(request.RequestUri.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(original) });
            }
            var content = request.RequestUri.AbsolutePath switch
            {
                "/token" => JsonContent.Create(new { access_token = "test-token" }),
                "/oauth2/userinfo" => JsonContent.Create(new { sub = "avatar-subject", profile = new { picture = avatarSource } }),
                _ => throw new InvalidOperationException($"Unexpected test request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };

        var registration = await ExchangeAsync(fixture);
        Assert.NotNull(registration.RegistrationTicket);
        Assert.Empty(avatarDownloads);
        var created = await fixture.Identity.CompleteOAuthRegistrationAsync(registration.RegistrationTicket, new("avatar@example.test", "头像测试", "", ""));
        var account = await fixture.Context.Collection<UserAccount>("users").Find(user => user.Id == created.Id).SingleAsync();
        var revision = Assert.IsType<string>(account.AvatarRevision);
        var url = UserAvatarFiles.VersionedUrl(created.Id, revision);
        Assert.Equal(url, created.AvatarUrl);
        Assert.Equal(url, (await fixture.Identity.GetUserAsync(created.Id))!.AvatarUrl);

        foreach (var source in new[] { CDN_AVATAR_URL, "/" + PROVIDER_AVATAR_PATH })
        {
            avatarSource = source;
            var login = await ExchangeAsync(fixture);
            Assert.Equal(url, login.User!.AvatarUrl);
        }
        Assert.Equal(PROVIDER_AVATAR_URL, Assert.Single(avatarDownloads));
        var cached = await UserAvatarFiles.ReadAsync(fixture.Paths, created.Id, revision, CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal("image/gif", cached.ContentType);
        Assert.Equal(original, cached.Content);
        Assert.Equal($"\"{Convert.ToHexString(SHA256.HashData(original))}\"", cached.ETag);
        Assert.False(await fixture.Context.Collection<BsonDocument>("oauth_avatars").Find(new BsonDocument("_id", created.Id)).AnyAsync());
    }

    private static async Task<OAuthLoginResult> ExchangeAsync(IdentityFixture fixture)
    {
        var authorization = await fixture.Identity.BeginOAuthAsync("avatar-test", "https://recorder.example.test/oauth/callback/avatar-test", "avatar-browser");
        var state = new Uri(authorization.AuthorizationUrl).Query.TrimStart('?').Split('&')
            .Single(value => value.StartsWith("state=", StringComparison.Ordinal))[6..];
        return await fixture.Identity.CompleteOAuthAsync("avatar-test", Uri.UnescapeDataString(state), "authorization-code", "avatar-browser");
    }
}
