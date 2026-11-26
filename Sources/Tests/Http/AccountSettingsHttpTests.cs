using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过主站 HTTP 验证账户自助、邮箱确认和会话撤销。</summary>
public sealed class AccountSettingsHttpTests
{
    private const string SETTINGS = "/api/auth/me/settings";
    private const string DISPLAY_NAME = "/api/auth/me/display-name";
    private const string PASSWORD = "/api/auth/me/password";
    private const string REQUEST_EMAIL_CHANGE = "/api/auth/me/email-change/request";
    private const string CONFIRM_EMAIL_CHANGE = "/api/auth/me/email-change/confirm";
    private const string NEW_PASSWORD = "new-local-password-123";

    [MongoHttpFact]
    public async Task RenameKeepsTheUserIdAndPasswordChangeRevokesBothSessions()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var user = await fixture.CreateUserAsync("account-settings@example.test");
        var browser = fixture.NewBrowser();
        var other = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        await other.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var anonymous = await fixture.NewBrowser().Client.GetAsync(SETTINGS);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var settings = await browser.Client.GetFromJsonAsync<JsonObject>(SETTINGS);
        Assert.True(settings!["hasLocalPassword"]!.GetValue<bool>());
        Assert.False(settings["canChangeEmail"]!.GetValue<bool>());
        using var missingCsrf = await browser.SendAsync(HttpMethod.Put, DISPLAY_NAME, new { displayName = "新名称" }, includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var renamed = await browser.SendAsync(HttpMethod.Put, DISPLAY_NAME, new { displayName = "  新名称  " });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var updated = await renamed.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(user.Id, updated!["id"]!.GetValue<string>());
        Assert.Equal(user.Email, updated["email"]!.GetValue<string>());
        Assert.Equal("新名称", updated["displayName"]!.GetValue<string>());
        Assert.Equal("新名称", (await other.RefreshCsrfAsync())["user"]!["displayName"]!.GetValue<string>());

        using var invalid = await browser.SendAsync(HttpMethod.Put, PASSWORD, new { currentPassword = "incorrect-password", newPassword = NEW_PASSWORD });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.NotNull((await browser.RefreshCsrfAsync())["user"]);
        using var changed = await browser.SendAsync(HttpMethod.Put, PASSWORD, new { currentPassword = HttpServerFixture.USER_PASSWORD, newPassword = NEW_PASSWORD });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        await AssertSignedOutAsync(browser, other);
        await AssertRejectedLoginAsync(browser, user.Email, HttpServerFixture.USER_PASSWORD);
        var login = await browser.LoginAsync(user.Email, NEW_PASSWORD);
        Assert.Equal(user.Id, login["user"]!["id"]!.GetValue<string>());
        Assert.Equal("新名称", login["user"]!["displayName"]!.GetValue<string>());
    }

    [MongoHttpFact]
    public async Task NewEmailRequiresItsOwnSingleUseCodeAndRevokesBothSessions()
    {
        await using var smtp = new PasswordResetSmtpServer();
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await ConfigureAsync(fixture, smtp);
        var user = await fixture.CreateUserAsync("before-rebind@example.test");
        var browser = fixture.NewBrowser();
        var other = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        await other.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        const string NEW_EMAIL = "new-address@example.test";

        var settings = await browser.Client.GetFromJsonAsync<JsonObject>(SETTINGS);
        Assert.True(settings!["canChangeEmail"]!.GetValue<bool>());
        using var request = await browser.SendAsync(HttpMethod.Post, REQUEST_EMAIL_CHANGE,
            new { email = " New-Address@Example.Test ", currentPassword = HttpServerFixture.USER_PASSWORD });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var pending = await request.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(NEW_EMAIL, pending!["email"]!.GetValue<string>());
        Assert.Equal(new[] { "email", "expiresAtUtc", "nextSendAtUtc" }, pending.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.True(pending["expiresAtUtc"]!.GetValue<DateTimeOffset>() > pending["nextSendAtUtc"]!.GetValue<DateTimeOffset>());
        var code = await ReadCodeAsync(smtp, NEW_EMAIL);
        Assert.Equal(user.Email, (await browser.RefreshCsrfAsync())["user"]!["email"]!.GetValue<string>());
        var pendingFromAnotherSession = (await other.Client.GetFromJsonAsync<JsonObject>(SETTINGS))!["pendingEmailChange"];
        Assert.Equal(NEW_EMAIL, pendingFromAnotherSession!["email"]!.GetValue<string>());
        await AssertRejectedLoginAsync(fixture.NewBrowser(), NEW_EMAIL, HttpServerFixture.USER_PASSWORD);

        var wrongCode = code == "123456" ? "654321" : "123456";
        using var wrong = await browser.SendAsync(HttpMethod.Post, CONFIRM_EMAIL_CHANGE, new { email = NEW_EMAIL, code = wrongCode });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var wrongEmail = await browser.SendAsync(HttpMethod.Post, CONFIRM_EMAIL_CHANGE, new { email = "another-address@example.test", code });
        Assert.Equal(HttpStatusCode.BadRequest, wrongEmail.StatusCode);
        using var confirmed = await browser.SendAsync(HttpMethod.Post, CONFIRM_EMAIL_CHANGE, new { email = NEW_EMAIL, code });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        await AssertSignedOutAsync(browser, other);
        await AssertRejectedLoginAsync(browser, user.Email, HttpServerFixture.USER_PASSWORD);
        var login = await browser.LoginAsync(NEW_EMAIL, HttpServerFixture.USER_PASSWORD);
        Assert.Equal(user.Id, login["user"]!["id"]!.GetValue<string>());
        using var replay = await browser.SendAsync(HttpMethod.Post, CONFIRM_EMAIL_CHANGE, new { email = NEW_EMAIL, code });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        var stored = await fixture.Context.Collection<UserAccount>("users").Find(candidate => candidate.Id == user.Id).SingleAsync();
        Assert.Equal(NEW_EMAIL, stored.NormalizedEmail);
        Assert.True(stored.EmailVerified);
        Assert.Equal(1, stored.SessionVersion);
    }

    [MongoHttpFact]
    public async Task OAuthOnlyAccountMustReauthorizeTheSameSubjectToChangeEmail()
    {
        await using var smtp = new PasswordResetSmtpServer();
        await using var provider = await OAuthTestProvider.CreateAsync();
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await ConfigureAsync(fixture, smtp, provider);
        var user = await fixture.CreateUserAsync("oauth-rebind@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var bind = await OAuthExchangeAsync(browser, provider, "bind");
        Assert.Equal(HttpStatusCode.OK, bind.StatusCode);
        await fixture.Context.Collection<UserAccount>("users").UpdateOneAsync(candidate => candidate.Id == user.Id,
            Builders<UserAccount>.Update.Set(candidate => candidate.PasswordHash, ""));
        await browser.RefreshCsrfAsync();
        var settings = await browser.Client.GetFromJsonAsync<JsonObject>(SETTINGS);
        Assert.False(settings!["hasLocalPassword"]!.GetValue<bool>());
        Assert.True(settings["canChangeEmail"]!.GetValue<bool>());
        const string NEW_EMAIL = "new-oauth-address@example.test";
        using var direct = await browser.SendAsync(HttpMethod.Post, REQUEST_EMAIL_CHANGE, new { email = NEW_EMAIL });
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);

        var linkedSubject = provider.Subject;
        provider.Subject = "different-external-subject";
        using var wrongSubject = await OAuthExchangeAsync(browser, provider, "email-change", new { email = NEW_EMAIL });
        Assert.Equal(HttpStatusCode.Forbidden, wrongSubject.StatusCode);
        Assert.False(smtp.Message.IsCompleted);
        provider.Subject = linkedSubject;
        using var verifiedSubject = await OAuthExchangeAsync(browser, provider, "email-change", new { email = NEW_EMAIL });
        Assert.Equal(HttpStatusCode.OK, verifiedSubject.StatusCode);
        var result = await verifiedSubject.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(result!["emailChangeStarted"]!.GetValue<bool>());
        Assert.Equal(user.Email, (await browser.RefreshCsrfAsync())["user"]!["email"]!.GetValue<string>());
        var code = await ReadCodeAsync(smtp, NEW_EMAIL);
        using var confirmed = await browser.SendAsync(HttpMethod.Post, CONFIRM_EMAIL_CHANGE, new { email = NEW_EMAIL, code });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        await AssertSignedOutAsync(browser);
        await browser.RefreshCsrfAsync();
        using var nextLogin = await OAuthExchangeAsync(browser, provider, "begin");
        Assert.Equal(HttpStatusCode.OK, nextLogin.StatusCode);
        var login = await nextLogin.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(user.Id, login!["user"]!["id"]!.GetValue<string>());
        Assert.Equal(NEW_EMAIL, login["user"]!["email"]!.GetValue<string>());
    }

    private static async Task ConfigureAsync(HttpServerFixture fixture, PasswordResetSmtpServer smtp, OAuthTestProvider? provider = null)
    {
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options)
            ?? throw new InvalidDataException("Missing settings.");
        settings.RegistrationEnabled = false;
        settings.RegistrationEmailVerificationRequired = false;
        settings.Smtp.Host = "127.0.0.1";
        settings.Smtp.Port = smtp.Port;
        settings.Smtp.FromAddress = "recorder@example.test";
        if (provider is not null)
        {
            settings.OAuthProviders.Add(new()
            {
                Id = "test", Name = "Test provider", Enabled = true, ClientId = "http-client",
                AuthorizationEndpoint = new Uri(provider.Address, "/oauth2/authorize").AbsoluteUri,
                TokenEndpoint = new Uri(provider.Address, "/oauth2/token").AbsoluteUri,
                UserInfoEndpoint = new Uri(provider.Address, "/oauth2/userinfo").AbsoluteUri,
            });
        }
        using var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private static async Task<HttpResponseMessage> OAuthExchangeAsync(HttpBrowser browser, OAuthTestProvider provider, string action, object? body = null)
    {
        using var start = await browser.SendAsync(HttpMethod.Post, $"/api/auth/oauth/test/{action}", body);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var authorization = await start.Content.ReadFromJsonAsync<JsonObject>();
        var query = QueryHelpers.ParseQuery(new Uri(authorization!["authorizationUrl"]!.GetValue<string>()).Query);
        provider.ExpectedChallenge = query["code_challenge"].ToString();
        return await browser.SendAsync(HttpMethod.Post, "/api/auth/oauth/test/exchange", new { state = query["state"].ToString(), code = "http-authorization-code" });
    }

    private static async Task<string> ReadCodeAsync(PasswordResetSmtpServer smtp, string email)
    {
        var body = await smtp.Message.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(email, smtp.Recipient);
        return Assert.Single(Regex.Matches(body, @"(?<![0-9])[0-9]{6}(?![0-9])")).Value;
    }

    private static async Task AssertSignedOutAsync(params HttpBrowser[] browsers)
    {
        foreach (var browser in browsers)
        {
            using var settings = await browser.Client.GetAsync(SETTINGS);
            Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);
        }
    }

    private static async Task AssertRejectedLoginAsync(HttpBrowser browser, string email, string password)
    {
        await browser.RefreshCsrfAsync();
        using var login = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
