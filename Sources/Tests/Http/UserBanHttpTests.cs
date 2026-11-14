using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Server.Realtime;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>使用真实 Cookie 和 WebSocket 验证封禁与解封的会话边界。</summary>
public sealed class UserBanHttpTests
{
    /// <summary>封禁期间未发送请求的旧 Cookie 在解封后同样失效。</summary>
    [MongoHttpFact]
    public async Task BanAndUnbanRejectBothCurrentAndDormantLegacyCookies()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("banned@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        var dormant = fixture.NewBrowser();
        var identity = (ClaimsIdentity)UserPrincipalFactory.Create(new(user.Id, user.Email, user.DisplayName, "", "User", "Active")).Identity!;
        identity.RemoveClaim(identity.FindFirst(UserPrincipalFactory.SESSION_VERSION_CLAIM)!);
        var options = fixture.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, CookieAuthenticationDefaults.AuthenticationScheme);
        dormant.Cookies.Add(fixture.Address, new Cookie("lr.session", options.TicketDataFormat.Protect(ticket), "/"));
        using (var legacy = await dormant.Client.GetAsync("/api/sessions"))
        {
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        }
        var records = fixture.Context.Collection<MediaJob>("jobs");
        var job = new MediaJob { OwnerId = user.Id, Kind = "Clip", State = "Completed" };
        await records.InsertOneAsync(job);
        using (var ban = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{user.Id}/ban"))
        {
            Assert.Equal(HttpStatusCode.OK, ban.StatusCode);
            Assert.Equal("Disabled", (await ban.Content.ReadFromJsonAsync<UserProfile>(JsonFiles.Options))!.Status);
        }
        using (var denied = await browser.Client.GetAsync("/api/sessions"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (var approval = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{user.Id}/approval", new { approved = true }))
        {
            Assert.Equal(HttpStatusCode.Conflict, approval.StatusCode);
        }
        using (var unban = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{user.Id}/unban"))
        {
            Assert.Equal(HttpStatusCode.OK, unban.StatusCode);
        }
        using (var old = await dormant.Client.GetAsync("/api/sessions"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }
        await dormant.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var restored = await dormant.Client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("Completed", (await records.Find(item => item.Id == job.Id).SingleAsync()).State);
        Assert.Equal(user.Permissions, (await fixture.Context.Collection<UserAccount>("users").Find(item => item.Id == user.Id).SingleAsync()).Permissions);
    }

    /// <summary>权限和 CSRF 检查阻止匿名、普通用户和自封请求。</summary>
    [MongoHttpFact]
    public async Task BanRequiresAdministratorCsrfAndCannotTargetSelf()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var admin = await fixture.Context.Collection<UserAccount>("users").Find(item => item.Role == "Admin").SingleAsync();
        var user = await fixture.CreateUserAsync("member@example.test");
        var viewer = fixture.NewBrowser();
        await viewer.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var denied = await viewer.SendAsync(HttpMethod.Post, $"/api/admin/users/{admin.Id}/ban");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var self = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{admin.Id}/ban");
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        using var csrf = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{user.Id}/ban", includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var missing = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/admin/users/missing/ban");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>封禁提交后主动断开真实已连接的状态 WebSocket。</summary>
    [MongoHttpFact]
    public async Task BanImmediatelyClosesExistingStatusConnection()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("socket@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        socket.Options.Cookies = browser.Cookies;
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/hubs/status" }.Uri, timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[1024];
        var handshake = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Contains("{}", Encoding.UTF8.GetString(buffer, 0, handshake.Count));
        var connections = fixture.App.Services.GetRequiredService<StatusConnections>();
        while (!connections.All.Any(connection => connection.UserId == user.Id))
        {
            await Task.Delay(10, timeout.Token);
        }
        using var response = await fixture.Admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{user.Id}/ban");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(connections.All, connection => connection.UserId == user.Id);
        try
        {
            while ((await socket.ReceiveAsync(buffer, timeout.Token)).MessageType != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException)
        {
            Assert.DoesNotContain(connections.All, connection => connection.UserId == user.Id);
        }
    }
}
