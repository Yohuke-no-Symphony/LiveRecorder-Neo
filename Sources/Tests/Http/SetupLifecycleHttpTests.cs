using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证安装向导在磁盘和旧浏览器凭据上均保持只读。</summary>
public sealed class SetupLifecycleHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstWizardRequestsAndCsrfDoNotCreateDataRecordsKeysOrInstallationTokens(bool hasDescriptor)
    {
        await using var fixture = hasDescriptor ? await HttpServerFixture.CreateAsync() : await HttpServerFixture.CreateWithoutConfigurationAsync();
        AssertAbsent(fixture);
        var state = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/setup/status");
        Assert.False(state!["installed"]!.GetValue<bool>());
        AssertAbsent(fixture);
        var ui = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/ui-settings");
        Assert.Equal("zh-CN", ui!["defaultLanguage"]!.GetValue<string>());
        AssertAbsent(fixture);
        var me = await fixture.Admin.RefreshCsrfAsync();
        Assert.Null(me["user"]);
        Assert.False(string.IsNullOrEmpty(fixture.Admin.CsrfToken));
        AssertAbsent(fixture);
        Assert.Contains(fixture.Admin.Cookies.GetCookies(fixture.Address).Cast<Cookie>(), cookie => cookie.Name.StartsWith("lr.setup.csrf.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForeignSessionAndLegacyCsrfCookiesAreNotDecryptedByAnUninstalledHost()
    {
        await using var fixture = await HttpServerFixture.CreateWithoutConfigurationAsync();
        var (session, csrf) = ForeignCookies();
        foreach (var path in new[] { "/api/setup/status", "/api/ui-settings", "/api/auth/me" })
        {
            var browser = fixture.NewBrowser();
            browser.Cookies.Add(fixture.Address, new Cookie("lr.session", session, "/"));
            browser.Cookies.Add(fixture.Address, new Cookie("lr.csrf", csrf, "/"));
            using var probe = new RequestCryptographyProbe();

            using var response = await probe.GetAsync(browser.Client, path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonObject>();
            if (path == "/api/auth/me")
            {
                Assert.Null(body!["user"]);
                Assert.False(string.IsNullOrWhiteSpace(body["csrfToken"]!.GetValue<string>()));
            }
            Assert.True(probe.ObservedServerActivity);
            Assert.Equal(0, probe.CryptographicFailures);
            AssertAbsent(fixture);
        }
    }

    [MongoHttpFact]
    public async Task SuccessfulInstallationDeletesForeignCookiesWithoutTryingToDecryptTheirTickets()
    {
        await using var fixture = await HttpServerFixture.CreateWithoutConfigurationAsync();
        await fixture.Admin.RefreshCsrfAsync();
        var (session, csrf) = ForeignCookies();
        fixture.Admin.Cookies.Add(fixture.Address, new Cookie("lr.session", session, "/"));
        fixture.Admin.Cookies.Add(fixture.Address, new Cookie("lr.csrf", csrf, "/"));
        fixture.TrackDatabase();
        var request = fixture.SetupRequest() with
        {
            DataDirectory = Path.Combine(fixture.DirectoryPath, "committed-data"),
            RecordsDirectory = Path.Combine(fixture.DirectoryPath, "committed-records")
        };
        using var probe = new RequestCryptographyProbe();

        using var response = await probe.PostAsync(fixture.Admin, "/api/setup", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonObject>())!["installed"]!.GetValue<bool>());
        Assert.True(probe.ObservedServerActivity);
        Assert.Equal(0, probe.CryptographicFailures);
        Assert.Null(fixture.Admin.Cookies.GetCookies(fixture.Address)["lr.session"]);
        Assert.Null(fixture.Admin.Cookies.GetCookies(fixture.Address)["lr.csrf"]);
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.Equal(request.DataDirectory, fixture.DataDirectory);
        Assert.Equal(request.RecordsDirectory, fixture.StoragePath);
    }

    private static (string Session, string Csrf) ForeignCookies()
    {
        var foreign = new EphemeralDataProtectionProvider();
        var ticketProtector = foreign.CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware")
            .CreateProtector(CookieAuthenticationDefaults.AuthenticationScheme).CreateProtector("v2");
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "foreign-user")], CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, CookieAuthenticationDefaults.AuthenticationScheme);
        return (new TicketDataFormat(ticketProtector).Protect(ticket), foreign.CreateProtector("Microsoft.AspNetCore.Antiforgery.AntiforgeryToken.v1").Protect("foreign-csrf-payload"));
    }

    private static void AssertAbsent(HttpServerFixture fixture)
    {
        Assert.False(Directory.Exists(fixture.DataDirectory));
        Assert.False(Directory.Exists(fixture.StoragePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "keys")));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "cache")));
        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "bootstrap.json")));
        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "installation-token")));
    }
}
