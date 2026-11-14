using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>真实 HTTP 验证显式绑定回调和受信任代理的 HTTPS 回调地址。</summary>
public sealed class OAuthHttpTests
{
    private const string FRONTEND_ORIGIN = "http://127.0.0.1:5784";

    [MongoHttpFact]
    public async Task LoggedInAccountCanBindThroughRealPkceExchangeWithoutRegisteringAnotherUser()
    {
        await using var provider = await OAuthTestProvider.CreateAsync();
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var login = await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var originalUser = login["user"]!["id"]!.GetValue<string>();
        await ConfigureProviderAsync(fixture, provider);
        Assert.Empty((await fixture.Admin.Client.GetFromJsonAsync<string[]>("/api/auth/identities"))!);
        using var start = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/auth/oauth/test/bind");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var payload = await start.Content.ReadFromJsonAsync<JsonObject>();
        var query = Query(new Uri(payload!["authorizationUrl"]!.GetValue<string>()).Query);
        provider.ExpectedChallenge = query["code_challenge"];
        using var exchange = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/auth/oauth/test/exchange", new { state = query["state"], code = "http-authorization-code" });
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        var result = await exchange.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(originalUser, result!["user"]!["id"]!.GetValue<string>());
        Assert.False(result["registrationRequired"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(result["csrfToken"]!.GetValue<string>()));
        Assert.Equal(new[] { "test" }, await fixture.Admin.Client.GetFromJsonAsync<string[]>("/api/auth/identities"));
        Assert.Equal(1, await fixture.Context.Collection<UserAccount>("users").CountDocumentsAsync(FilterDefinition<UserAccount>.Empty));
        using var replay = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/auth/oauth/test/exchange", new { state = query["state"], code = "http-authorization-code" });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    /// <summary>封禁后的外部身份换码不能创建会话，解封后必须通过新的授权流程登录。</summary>
    [MongoHttpFact]
    public async Task BannedExternalIdentityCannotSignInUntilUnbannedAndAuthorizedAgain()
    {
        await using var provider = await OAuthTestProvider.CreateAsync();
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await ConfigureProviderAsync(fixture, provider);
        var user = await fixture.CreateUserAsync("oauth-member@example.test");
        var linked = fixture.NewBrowser();
        await linked.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using (var binding = await ExchangeAsync(linked, provider, "bind"))
        {
            Assert.Equal(HttpStatusCode.OK, binding.StatusCode);
            var result = await binding.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(user.Id, result!["user"]!["id"]!.GetValue<string>());
        }
        using (var ban = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{user.Id}/ban"))
        {
            Assert.Equal(HttpStatusCode.OK, ban.StatusCode);
        }
        var browser = fixture.NewBrowser();
        await browser.RefreshCsrfAsync();
        using (var denied = await ExchangeAsync(browser, provider, "begin"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Null(browser.Cookies.GetCookies(fixture.Address)["lr.session"]);
        Assert.Null((await browser.RefreshCsrfAsync())["user"]);
        using (var recordings = await browser.Client.GetAsync("/api/sessions"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, recordings.StatusCode);
        }
        using (var unban = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{user.Id}/unban"))
        {
            Assert.Equal(HttpStatusCode.OK, unban.StatusCode);
        }
        using (var restored = await ExchangeAsync(browser, provider, "begin"))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            var result = await restored.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(user.Id, result!["user"]!["id"]!.GetValue<string>());
            Assert.False(result["registrationRequired"]!.GetValue<bool>());
        }
        Assert.Equal(user.Id, (await browser.RefreshCsrfAsync())["user"]!["id"]!.GetValue<string>());
        using (var recordings = await browser.Client.GetAsync("/api/sessions"))
        {
            Assert.Equal(HttpStatusCode.OK, recordings.StatusCode);
        }
        Assert.Equal(new[] { "test" }, await browser.Client.GetFromJsonAsync<string[]>("/api/auth/identities"));
        Assert.Equal(2, await fixture.Context.Collection<UserAccount>("users").CountDocumentsAsync(FilterDefinition<UserAccount>.Empty));
    }

    [MongoHttpFact]
    public async Task TrustedLoopbackProxyPreservesExternalHttpsCallback()
    {
        await using var provider = await OAuthTestProvider.CreateAsync();
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await ConfigureProviderAsync(fixture, provider);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/oauth/test/begin");
        request.Headers.Host = "recorder.example.test";
        request.Headers.Add("Origin", FRONTEND_ORIGIN);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-CSRF-TOKEN", fixture.Admin.CsrfToken);
        using var response = await fixture.Admin.Client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Unexpected proxy callback status {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        var query = Query(new Uri(body!["authorizationUrl"]!.GetValue<string>()).Query);
        Assert.Equal("https://recorder.example.test/oauth/callback/test", query["redirect_uri"]);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith("lr.oauth.binding=", StringComparison.Ordinal) && x.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [MongoHttpFact]
    public async Task DevelopmentOAuthReturnsToTrustedFrontEndForLoginAndBinding()
    {
        await using var provider = await OAuthTestProvider.CreateAsync();
        await using var fixture = await HttpServerFixture.CreateAsync("Development", "--WebUI:DevelopmentOrigins:0", FRONTEND_ORIGIN);
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await ConfigureProviderAsync(fixture, provider);
        fixture.Admin.Client.DefaultRequestHeaders.Add("Origin", FRONTEND_ORIGIN);
        foreach (var action in new[] { "begin", "bind" })
        {
            using var response = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/auth/oauth/test/{action}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonObject>();
            var query = Query(new Uri(body!["authorizationUrl"]!.GetValue<string>()).Query);
            Assert.Equal(FRONTEND_ORIGIN + "/oauth/callback/test", query["redirect_uri"]);
        }
        fixture.Admin.Client.DefaultRequestHeaders.Remove("Origin");
        fixture.Admin.Client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using var untrusted = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/auth/oauth/test/begin");
        Assert.Equal(HttpStatusCode.OK, untrusted.StatusCode);
        var result = await untrusted.Content.ReadFromJsonAsync<JsonObject>();
        var untrustedQuery = Query(new Uri(result!["authorizationUrl"]!.GetValue<string>()).Query);
        Assert.Equal(new Uri(fixture.Address, "/oauth/callback/test").AbsoluteUri, untrustedQuery["redirect_uri"]);
    }

    private static async Task ConfigureProviderAsync(HttpServerFixture fixture, OAuthTestProvider provider)
    {
        var settings = await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options) ?? throw new InvalidDataException("Missing settings.");
        settings.OAuthProviders.Add(new() { Id = "test", Name = "Test provider", Enabled = true, ClientId = "http-client", AuthorizationEndpoint = new Uri(provider.Address, "/oauth2/authorize").AbsoluteUri, TokenEndpoint = new Uri(provider.Address, "/oauth2/token").AbsoluteUri, UserInfoEndpoint = new Uri(provider.Address, "/oauth2/userinfo").AbsoluteUri });
        using var response = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(HttpBrowser browser, OAuthTestProvider provider, string action)
    {
        using var start = await browser.SendAsync(HttpMethod.Post, $"/api/auth/oauth/test/{action}");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var payload = await start.Content.ReadFromJsonAsync<JsonObject>();
        var query = Query(new Uri(payload!["authorizationUrl"]!.GetValue<string>()).Query);
        provider.ExpectedChallenge = query["code_challenge"];
        return await browser.SendAsync(HttpMethod.Post, "/api/auth/oauth/test/exchange", new { state = query["state"], code = "http-authorization-code" });
    }

    private static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
}
