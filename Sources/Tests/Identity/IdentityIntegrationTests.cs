using System.Net;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>在唯一临时数据库验证安装、注册门禁、审核和授权状态。</summary>
public sealed class IdentityIntegrationTests
{
    [MongoFact]
    public async Task InstallationCreatesOneHashedAdministratorAndCannotBeRepeated()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        Assert.True(fixture.Bootstrap.IsInstalled);
        var admin = await fixture.Identity.AuthenticateAsync(new("ADMIN@EXAMPLE.TEST", IdentityFixture.ADMIN_PASSWORD));
        Assert.Equal("Admin", admin.Role);
        Assert.Equal("Active", admin.Status);
        var stored = await fixture.Context.Collection<UserAccount>("users").Find(x => x.Id == admin.Id).SingleAsync();
        Assert.NotEqual(IdentityFixture.ADMIN_PASSWORD, stored.PasswordHash);
        Assert.DoesNotContain(fixture.ConnectionString, await File.ReadAllTextAsync(Path.Combine(fixture.DirectoryPath, "data", "bootstrap.json")));
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Installation.InstallAsync(fixture.InstallRequest()));
        Assert.Equal(409, error.StatusCode);
        Assert.Single(await fixture.Identity.ListUsersAsync());
    }

    [MongoFact]
    public async Task ClosedRegistrationDoesNotSendEmailOrCreateUsers()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SendEmailCodeAsync(new("viewer@example.test")));
        Assert.Equal(403, error.StatusCode);
        Assert.Empty(fixture.Email.Messages);
        Assert.Single(await fixture.Identity.ListUsersAsync());
    }

    [MongoFact]
    public async Task RegistrationConsumesInvitationAndCannotAuthenticateBeforeApproval()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.EnableRegistrationAsync();
        var invitation = await fixture.Identity.CreateInvitationAsync(new(), "admin");
        var code = await fixture.SendCodeAsync("viewer@example.test");
        var user = await fixture.Identity.RegisterAsync(new("viewer@example.test", IdentityFixture.USER_PASSWORD, "观众", code, invitation.Code!));
        Assert.Equal("Pending", user.Status);
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id));
        Assert.Equal(403, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.AuthenticateAsync(new(user.Email, IdentityFixture.USER_PASSWORD)))).StatusCode);
        await fixture.Identity.SetApprovalAsync(user.Id, true);
        Assert.NotNull(await fixture.Identity.ValidateSessionAsync(user.Id));
        Assert.Equal(user.Id, (await fixture.Identity.AuthenticateAsync(new(user.Email, IdentityFixture.USER_PASSWORD))).Id);
        await fixture.Identity.SetApprovalAsync(user.Id, false);
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id));
        Assert.Equal(1, Assert.Single(await fixture.Identity.ListInvitationsAsync()).UsedCount);
        var secondCode = await fixture.SendCodeAsync("second@example.test");
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.RegisterAsync(new("second@example.test", IdentityFixture.USER_PASSWORD, "第二位", secondCode, invitation.Code!)));
        Assert.Equal(2, (await fixture.Identity.ListUsersAsync()).Count);
    }

    [MongoFact]
    public async Task ParallelRegistrationsCannotOverspendSingleUseInvitation()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.EnableRegistrationAsync();
        var invitation = await fixture.Identity.CreateInvitationAsync(new(), "admin");
        var firstCode = await fixture.SendCodeAsync("first@example.test");
        var secondCode = await fixture.SendCodeAsync("second@example.test");
        async Task<bool> RegisterAsync(string email, string code)
        {
            try
            {
                await fixture.Identity.RegisterAsync(new(email, IdentityFixture.USER_PASSWORD, "观众", code, invitation.Code!));
                return true;
            }
            catch (InfrastructureException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(RegisterAsync("first@example.test", firstCode), RegisterAsync("second@example.test", secondCode));
        Assert.Single(results, x => x);
        Assert.Equal(1, Assert.Single(await fixture.Identity.ListInvitationsAsync()).UsedCount);
        Assert.Equal(2, (await fixture.Identity.ListUsersAsync()).Count);
    }

    [MongoFact]
    public async Task EmailCodeAttemptBudgetAndResendCooldownAreEnforced()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.EnableRegistrationAsync();
        var invitation = await fixture.Identity.CreateInvitationAsync(new(), "admin");
        var code = await fixture.SendCodeAsync("viewer@example.test");
        Assert.Equal(429, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SendEmailCodeAsync(new("viewer@example.test")))).StatusCode);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.RegisterAsync(new("viewer@example.test", IdentityFixture.USER_PASSWORD, "观众", "000000", invitation.Code!)));
        }
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.RegisterAsync(new("viewer@example.test", IdentityFixture.USER_PASSWORD, "观众", code, invitation.Code!)));
        Assert.Equal(0, Assert.Single(await fixture.Identity.ListInvitationsAsync()).UsedCount);
    }

    [MongoFact]
    public async Task LastActiveAdministratorCannotBeRejectedOrDemoted()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var admin = Assert.Single(await fixture.Identity.ListUsersAsync());
        Assert.Equal(409, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetApprovalAsync(admin.Id, false))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.SetRoleAsync(admin.Id, "User"))).StatusCode);
        Assert.Equal("Admin", (await fixture.Identity.ValidateSessionAsync(admin.Id))!.Role);
    }

    [MongoFact]
    public async Task OAuthStateIsBrowserBoundSingleUseAndNewUsersStillPassAllRegistrationGates()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.EnableRegistrationAsync();
        var settings = await fixture.Settings.GetAsync();
        settings.OAuthProviders.Add(new() { Id = "yohuke", Enabled = true, ClientId = "test-client", ClientSecret = fixture.Protector.Protect("test-secret"), AuthorizationEndpoint = "https://userservice.example.test/oauth2/authorize", TokenEndpoint = "https://userservice.example.test/oauth2/token", UserInfoEndpoint = "https://userservice.example.test/oauth2/userinfo" });
        await fixture.Settings.SaveAsync(settings);
        string? expectedChallenge = null;
        var httpCalls = 0;
        fixture.Http.Handle = async request =>
        {
            httpCalls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = Query(await request.Content!.ReadAsStringAsync());
                var actual = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Assert.Equal(expectedChallenge, actual);
                Assert.Equal("test-secret", form["client_secret"]);
                return Json("""{"access_token":"test-access-token","token_type":"Bearer"}""");
            }
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Json("""{"sub":"stable-external-id","displayName":"外部用户","email":"external@example.test","emailVerified":true}""");
        };
        var authorization = await fixture.Identity.BeginOAuthAsync("yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-binding");
        var query = Query(new Uri(authorization.AuthorizationUrl).Query.TrimStart('?'));
        expectedChallenge = query["code_challenge"];
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", query["state"], "authorization-code", "other-browser"));
        Assert.Equal(0, httpCalls);
        var login = await fixture.Identity.CompleteOAuthAsync("yohuke", query["state"], "authorization-code", "browser-binding");
        Assert.Null(login.User);
        Assert.NotNull(login.RegistrationTicket);
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthAsync("yohuke", query["state"], "authorization-code", "browser-binding"));
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Identity.CompleteOAuthRegistrationAsync(login.RegistrationTicket!, new("external@example.test", "外部用户", "", "")));
        var invitation = await fixture.Identity.CreateInvitationAsync(new(), "admin");
        var emailCode = await fixture.SendCodeAsync("external@example.test");
        var user = await fixture.Identity.CompleteOAuthRegistrationAsync(login.RegistrationTicket!, new("external@example.test", "外部用户", emailCode, invitation.Code!));
        Assert.Equal("Pending", user.Status);
        Assert.Null(await fixture.Identity.ValidateSessionAsync(user.Id));
        await fixture.Identity.SetApprovalAsync(user.Id, true);
        var next = await fixture.Identity.BeginOAuthAsync("yohuke", "https://recorder.example.test/oauth/callback/yohuke", "browser-binding");
        query = Query(new Uri(next.AuthorizationUrl).Query.TrimStart('?'));
        expectedChallenge = query["code_challenge"];
        var existing = await fixture.Identity.CompleteOAuthAsync("yohuke", query["state"], "next-code", "browser-binding");
        Assert.Equal(user.Id, existing.User!.Id);
        Assert.Null(existing.RegistrationTicket);
    }

    private static Dictionary<string, string> Query(string value) => value.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
