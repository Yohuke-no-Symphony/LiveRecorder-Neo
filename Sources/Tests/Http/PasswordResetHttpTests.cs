using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Server.Realtime;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>通过真实 SMTP、HTTP 和 Cookie 验证邮件密码恢复。</summary>
public sealed class PasswordResetHttpTests
{
    /// <summary>邮件始终使用可信配置地址，成功改密撤销所有旧登录并只消费一次。</summary>
    [MongoHttpFact]
    public async Task EmailLinkResetsPasswordAndRevokesEveryOldSession()
    {
        await using var smtp = new PasswordResetSmtpServer();
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await ConfigureAsync(fixture, smtp.Port);
        var user = await fixture.CreateUserAsync("recovery@example.test");
        var browser = fixture.NewBrowser();
        var dormant = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        await dormant.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var anonymous = fixture.NewBrowser();
        await anonymous.RefreshCsrfAsync();
        var options = await anonymous.Client.GetFromJsonAsync<PasswordResetOptions>("/api/auth/password-reset/options");
        Assert.True(options!.Enabled);
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password-reset/request"))
        {
            request.Headers.Host = "attacker.example.test";
            request.Headers.Add("X-CSRF-TOKEN", anonymous.CsrfToken);
            request.Content = JsonContent.Create(new { email = user.Email });
            using var response = await anonymous.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        var body = await smtp.Message.WaitAsync(TimeSpan.FromSeconds(10));
        var link = new Uri(Regex.Match(body, @"https://\S+").Value);
        Assert.Equal("recorder.example.test", link.Host);
        Assert.Equal("/reset-password", link.AbsolutePath);
        Assert.Equal("", link.Query);
        var token = link.Fragment["#token=".Length..];
        Assert.Equal(43, token.Length);
        Assert.DoesNotContain("attacker.example.test", body);
        var connections = fixture.App.Services.GetRequiredService<StatusConnections>();
        var disconnected = false;
        connections.Add(new("reset-old-connection", user.Id, 0, () => disconnected = true));
        const string NEW_PASSWORD = "new-reset-password-123";
        using (var response = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/password-reset/complete", new { token, newPassword = NEW_PASSWORD }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.True(disconnected);
        foreach (var oldBrowser in new[] { browser, dormant })
        {
            using var oldSession = await oldBrowser.Client.GetAsync("/api/sessions");
            Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        }
        Assert.Null((await anonymous.RefreshCsrfAsync())["user"]);
        using (var replay = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/password-reset/complete", new { token, newPassword = "another-password-123" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        }
        using (var oldPassword = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = user.Email, password = HttpServerFixture.USER_PASSWORD }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        }
        await anonymous.LoginAsync(user.Email, NEW_PASSWORD);
        using var restored = await anonymous.Client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var stored = await fixture.Context.Collection<UserAccount>("users").Find(item => item.Id == user.Id).SingleAsync();
        Assert.True(stored.EmailVerified);
        Assert.Equal(1, stored.SessionVersion);
        Assert.Equal("Active", stored.Status);
    }

    /// <summary>未配置服务时统一提示不可用，配置地址必须是可信站点根。</summary>
    [MongoHttpFact]
    public async Task MissingConfigurationAndInvalidPublicUrlsAreRejected()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var anonymous = fixture.NewBrowser();
        await anonymous.RefreshCsrfAsync();
        Assert.False((await anonymous.Client.GetFromJsonAsync<PasswordResetOptions>("/api/auth/password-reset/options"))!.Enabled);
        foreach (var email in new[] { HttpServerFixture.ADMIN_EMAIL, "unknown@example.test" })
        {
            using var request = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/password-reset/request", new { email });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, request.StatusCode);
        }
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!;
        settings.PublicWebUrl = "https://recorder.example.test/untrusted?returnTo=https://attacker.example";
        using var invalid = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("", (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!.PublicWebUrl);
    }

    /// <summary>未知邮箱和冷却返回一致状态，发送预算和 CSRF 在 HTTP 层生效。</summary>
    [MongoHttpFact]
    public async Task RequestUsesCsrfAndPerIpRateLimitWithoutRevealingUnknownEmails()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        await ConfigureAsync(fixture, 1);
        var anonymous = fixture.NewBrowser();
        await anonymous.RefreshCsrfAsync();
        using (var missingCsrf = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/password-reset/complete", new { token = "invalid", newPassword = "valid-password-123" }, includeCsrf: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            var problem = await missingCsrf.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("REQUEST_CSRF_EXPIRED", problem!["code"]!.GetValue<string>());
        }
        for (var index = 0; index < 5; index++)
        {
            using var response = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/password-reset/request", new { email = "unknown@example.test" });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using var limited = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/password-reset/request", new { email = "different@example.test" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static async Task ConfigureAsync(HttpServerFixture fixture, int smtpPort)
    {
        var settings = (await fixture.Admin.Client.GetFromJsonAsync<ServerSettings>("/api/settings", JsonFiles.Options))!;
        settings.PublicWebUrl = "https://recorder.example.test/";
        settings.Smtp.Host = "127.0.0.1";
        settings.Smtp.Port = smtpPort;
        settings.Smtp.FromAddress = "recorder@example.test";
        settings.Smtp.UserName = "";
        settings.RegistrationEnabled = false;
        using var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("https://recorder.example.test", (await saved.Content.ReadFromJsonAsync<ServerSettings>(JsonFiles.Options))!.PublicWebUrl);
    }
}
