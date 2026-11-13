using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证管理员授权入口、独立策略以及已有 Cookie 的即时授撤权。</summary>
public sealed class UserPermissionsHttpTests
{
    /// <summary>真实宿主中的三个权限策略独立判断，管理员不依赖显式授权声明。</summary>
    [Fact]
    public async Task HostPoliciesRespectIndependentGrantsAndAdministratorRole()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var authorization = fixture.App.Services.GetRequiredService<IAuthorizationService>();
        var policies = new[] { UserPermissionNames.MANAGE_RECORDINGS, UserPermissionNames.VIEW_ANALYTICS, UserPermissionNames.PURGE_RECORDINGS };
        for (var mask = 0; mask < 8; mask++)
        {
            var user = new UserProfile("viewer", "viewer@example.test", "用户", "", "User", "Active")
            {
                Permissions = new() { ManageRecordings = (mask & 1) != 0, ViewAnalytics = (mask & 2) != 0, PurgeRecordings = (mask & 4) != 0 }
            };
            var principal = UserPrincipalFactory.Create(user);
            for (var index = 0; index < policies.Length; index++)
            {
                Assert.Equal((mask & (1 << index)) != 0, (await authorization.AuthorizeAsync(principal, null, policies[index])).Succeeded);
            }
        }
        var administrator = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "Test"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(policies.Select(permission => new Claim(UserPermissionNames.CLAIM_TYPE, permission))));
        foreach (var policy in policies)
        {
            Assert.True((await authorization.AuthorizeAsync(administrator, null, policy)).Succeeded);
            Assert.False((await authorization.AuthorizeAsync(anonymous, null, policy)).Succeeded);
        }
    }

    /// <summary>只有管理员可以编辑权限，获得全部能力的普通用户也不能自行授权。</summary>
    [MongoHttpFact]
    public async Task PermissionEditingRequiresAdministratorAndReturnsEffectiveUserProfile()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var adminSession = await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.True(adminSession["user"]!["permissions"]!["manageRecordings"]!.GetValue<bool>());
        Assert.True(adminSession["user"]!["permissions"]!["viewAnalytics"]!.GetValue<bool>());
        Assert.True(adminSession["user"]!["permissions"]!["purgeRecordings"]!.GetValue<bool>());
        var user = await fixture.CreateUserAsync("permissions-editor@example.test");
        var endpoint = $"/api/admin/users/{user.Id}/permissions";
        var grants = new UserPermissions { ManageRecordings = true, ViewAnalytics = true, PurgeRecordings = true };
        var browser = fixture.NewBrowser();
        await browser.RefreshCsrfAsync();
        using (var anonymous = await browser.SendAsync(HttpMethod.Put, endpoint, grants))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using (var denied = await browser.SendAsync(HttpMethod.Put, endpoint, grants))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var saved = await fixture.Admin.SendAsync(HttpMethod.Put, endpoint, grants))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var profile = await saved.Content.ReadFromJsonAsync<UserProfile>();
            Assert.Equal(grants, profile!.Permissions);
            Assert.Equal(user.Id, profile.Id);
            Assert.Equal("User", profile.Role);
        }
        Assert.Equal(grants, (await fixture.Context.Collection<UserAccount>("users").Find(account => account.Id == user.Id).SingleAsync()).Permissions);
        using (var stillDenied = await browser.SendAsync(HttpMethod.Put, endpoint, new UserPermissions()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);
        }
        using var missing = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/admin/users/missing/permissions", grants);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>已登录用户下一次请求即刷新权限声明，撤权无需重新登录。</summary>
    [MongoHttpFact]
    public async Task ExistingCookieReceivesAndLosesPermissionClaimsOnTheNextRequest()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var user = await fixture.CreateUserAsync("permissions-cookie@example.test");
        var browser = fixture.NewBrowser();
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        Assert.Empty(CookiePrincipal(fixture, browser).FindAll(UserPermissionNames.CLAIM_TYPE));
        var grant = new UserPermissions { ViewAnalytics = true };
        using (var granted = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{user.Id}/permissions", grant))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }
        var session = await browser.RefreshCsrfAsync();
        Assert.True(session["user"]!["permissions"]!["viewAnalytics"]!.GetValue<bool>());
        var principal = CookiePrincipal(fixture, browser);
        Assert.True(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.VIEW_ANALYTICS));
        Assert.False(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.MANAGE_RECORDINGS));
        Assert.False(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.PURGE_RECORDINGS));
        using (var revoked = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{user.Id}/permissions", new UserPermissions()))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }
        session = await browser.RefreshCsrfAsync();
        Assert.Equal(user.Id, session["user"]!["id"]!.GetValue<string>());
        Assert.False(session["user"]!["permissions"]!["viewAnalytics"]!.GetValue<bool>());
        Assert.Empty(CookiePrincipal(fixture, browser).FindAll(UserPermissionNames.CLAIM_TYPE));
    }

    private static ClaimsPrincipal CookiePrincipal(HttpServerFixture fixture, HttpBrowser browser)
    {
        var options = fixture.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var cookie = browser.Cookies.GetCookies(fixture.Address)[options.Cookie.Name!]!;
        var ticket = options.TicketDataFormat.Unprotect(cookie.Value);
        Assert.NotNull(ticket);
        return ticket.Principal;
    }
}
