using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>已有本地账号显式绑定外部身份的越权与重复提交回归。</summary>
public sealed class OAuthBindingIntegrationTests
{
    [MongoFact]
    public async Task BindingRequiresTheSameBrowserAndLoggedInUser()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await ConfigureAsync(fixture);
        var user = Assert.Single(await fixture.Identity.ListUsersAsync());
        var authorization = await fixture.Identity.BeginOAuthBindingAsync(user.Id, "yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-a");
        var state = State(authorization);
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", state, "code", "other-browser", user.Id));
        Assert.Equal(403, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", state, "code", "browser-a", "other-user"))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", state, "code", "browser-a"))).StatusCode);
        var result = await fixture.Identity.CompleteOAuthAsync("yohuke", state, "code", "browser-a", user.Id);
        Assert.Equal(user.Id, result.User!.Id);
        Assert.Null(result.RegistrationTicket);
        Assert.Equal(new[] { "yohuke" }, await fixture.Identity.ListExternalProvidersAsync(user.Id));
    }

    [MongoFact]
    public async Task RepeatedBindingIsIdempotentButEachStateCanOnlyBeConsumedOnce()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await ConfigureAsync(fixture);
        var user = Assert.Single(await fixture.Identity.ListUsersAsync());
        for (var index = 0; index < 2; index++)
        {
            var flow = await fixture.Identity.BeginOAuthBindingAsync(user.Id, "yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-a");
            var state = State(flow);
            Assert.Equal(user.Id, (await fixture.Identity.CompleteOAuthAsync("yohuke", state, "code", "browser-a", user.Id)).User!.Id);
            await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", state, "code", "browser-a", user.Id));
        }
        Assert.Equal(1, await fixture.Context.Collection<ExternalLogin>("external_logins").CountDocumentsAsync(FilterDefinition<ExternalLogin>.Empty));
    }

    [MongoFact]
    public async Task ExternalIdentityOwnedByAnotherUserCannotBeReassigned()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await ConfigureAsync(fixture);
        var first = Assert.Single(await fixture.Identity.ListUsersAsync());
        var firstFlow = await fixture.Identity.BeginOAuthBindingAsync(first.Id, "yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-a");
        await fixture.Identity.CompleteOAuthAsync("yohuke", State(firstFlow), "code", "browser-a", first.Id);
        var other = new UserAccount { Email = "other@example.test", NormalizedEmail = "other@example.test", DisplayName = "另一位", Status = "Active", EmailVerified = true };
        other.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(other, IdentityFixture.USER_PASSWORD);
        await fixture.Context.Collection<UserAccount>("users").InsertOneAsync(other);
        var next = await fixture.Identity.BeginOAuthBindingAsync(other.Id, "yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-b");
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", State(next), "code", "browser-b", other.Id));
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(await fixture.Identity.ListExternalProvidersAsync(other.Id));
        Assert.Equal(first.Id, (await fixture.Context.Collection<ExternalLogin>("external_logins").Find(FilterDefinition<ExternalLogin>.Empty).SingleAsync()).UserId);
    }

    [MongoFact]
    public async Task DisabledUserCannotFinishPreviouslyStartedBinding()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await ConfigureAsync(fixture);
        var user = Assert.Single(await fixture.Identity.ListUsersAsync());
        var flow = await fixture.Identity.BeginOAuthBindingAsync(user.Id, "yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-a");
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(x => x.Id == user.Id, Builders<UserAccount>.Update.Set(x => x.Status, "Disabled"));
        Assert.Equal(403, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", State(flow), "code", "browser-a", user.Id))).StatusCode);
        Assert.Equal(0, await fixture.Context.Collection<ExternalLogin>("external_logins").CountDocumentsAsync(FilterDefinition<ExternalLogin>.Empty));
    }

    [MongoFact]
    public async Task LoginNeverLinksAnExistingLocalAccountMerelyBecauseEmailMatches()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await ConfigureAsync(fixture);
        await fixture.EnableRegistrationAsync();
        var user = Assert.Single(await fixture.Identity.ListUsersAsync());
        var flow = await fixture.Identity.BeginOAuthAsync("yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-a");
        var login = await fixture.Identity.CompleteOAuthAsync("yohuke", State(flow), "code", "browser-a");
        Assert.Equal(user.Email, login.Email);
        Assert.Null(login.User);
        Assert.NotNull(login.RegistrationTicket);
        Assert.Empty(await fixture.Identity.ListExternalProvidersAsync(user.Id));
    }

    private static async Task ConfigureAsync(IdentityFixture fixture)
    {
        var settings = await fixture.Settings.GetAsync();
        settings.OAuthProviders.Add(new() { Id = "yohuke", Enabled = true, ClientId = "test-client", AuthorizationEndpoint = "https://userservice.example.test/oauth2/authorize", TokenEndpoint = "https://userservice.example.test/oauth2/token", UserInfoEndpoint = "https://userservice.example.test/oauth2/userinfo" });
        await fixture.Settings.SaveAsync(settings);
        fixture.Http.Handle = request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"test-token\",\"token_type\":\"Bearer\"}"
                : "{\"sub\":\"stable-subject\",\"email\":\"admin@example.test\",\"emailVerified\":true,\"displayName\":\"External\"}", Encoding.UTF8, "application/json")
        });
    }

    private static string State(OAuthAuthorizationResult value) => new Uri(value.AuthorizationUrl).Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).Where(x => x[0] == "state").Select(x => Uri.UnescapeDataString(x[1])).Single();
}
