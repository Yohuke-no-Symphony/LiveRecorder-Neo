using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证管理员授权入口、独立策略以及已有 Cookie 的即时授撤权。</summary>
public sealed class UserPermissionsHttpTests
{
    /// <summary>真实宿主中的权限策略独立判断，管理员不依赖显式授权声明。</summary>
    [Fact]
    public async Task HostPoliciesRespectIndependentGrantsAndAdministratorRole()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var authorization = fixture.App.Services.GetRequiredService<IAuthorizationService>();
        var policies = new[]
        {
            UserPermissionNames.MANAGE_RECORDINGS, UserPermissionNames.VIEW_ANALYTICS,
            UserPermissionNames.PURGE_RECORDINGS, UserPermissionNames.MANAGE_CLIPS
        };
        for (var mask = 0; mask < (1 << policies.Length); mask++)
        {
            var user = new UserProfile("viewer", "viewer@example.test", "用户", "", "User", "Active")
            {
                Permissions = new(policies.Where((_, index) => (mask & (1 << index)) != 0))
            };
            var principal = UserPrincipalFactory.Create(user);
            for (var index = 0; index < policies.Length; index++)
            {
                Assert.Equal((mask & (1 << index)) != 0,
                    (await authorization.AuthorizeAsync(principal, null, policies[index])).Succeeded);
            }
        }

        var administrator = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "Test"));
        var anonymous =
            new ClaimsPrincipal(new ClaimsIdentity(policies.Select(permission =>
                new Claim(UserPermissionNames.CLAIM_TYPE, permission))));
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
        var adminSession =
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        Assert.Contains(PermissionIds.MANAGE_RECORDINGS,
            adminSession["user"]!["permissions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Contains(PermissionIds.VIEW_ANALYTICS,
            adminSession["user"]!["permissions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Contains(PermissionIds.PURGE_RECORDINGS,
            adminSession["user"]!["permissions"]!.AsArray().Select(item => item!.GetValue<string>()));
        var user = await fixture.CreateUserAsync("permissions-editor@example.test");
        var endpoint = $"/api/admin/users/{user.Id}/permissions";
        var grants = PermissionCatalog.All;
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
            var profile = await saved.Content.ReadFromJsonAsync<UserProfile>(JsonFiles.Options);
            Assert.Equal(grants, profile!.Permissions);
            Assert.Equal(user.Id, profile.Id);
            Assert.Equal("User", profile.Role);
        }

        Assert.Equal(grants,
            (await fixture.Context.Collection<UserAccount>("users").Find(account => account.Id == user.Id)
                .SingleAsync()).Permissions);
        using (var stillDenied = await browser.SendAsync(HttpMethod.Put, endpoint, PermissionSet.Empty))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);
        }

        using var missing =
            await fixture.Admin.SendAsync(HttpMethod.Put, "/api/admin/users/missing/permissions", grants);
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
        Assert.Equal(PermissionSet.MemberDefaults.Count,
            CookiePrincipal(fixture, browser).FindAll(UserPermissionNames.CLAIM_TYPE).Count());
        var grant = new PermissionSet([PermissionIds.VIEW_ANALYTICS]);
        using (var granted =
               await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{user.Id}/permissions", grant))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }

        var session = await browser.RefreshCsrfAsync();
        Assert.Contains(PermissionIds.VIEW_ANALYTICS,
            session["user"]!["permissions"]!.AsArray().Select(item => item!.GetValue<string>()));
        var principal = CookiePrincipal(fixture, browser);
        Assert.True(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.VIEW_ANALYTICS));
        Assert.False(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.MANAGE_RECORDINGS));
        Assert.False(principal.HasClaim(UserPermissionNames.CLAIM_TYPE, UserPermissionNames.PURGE_RECORDINGS));
        using (var revoked = await fixture.Admin.SendAsync(HttpMethod.Put, $"/api/admin/users/{user.Id}/permissions",
                   PermissionSet.Empty))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }

        session = await browser.RefreshCsrfAsync();
        Assert.Equal(user.Id, session["user"]!["id"]!.GetValue<string>());
        Assert.DoesNotContain(PermissionIds.VIEW_ANALYTICS,
            session["user"]!["permissions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Empty(CookiePrincipal(fixture, browser).FindAll(UserPermissionNames.CLAIM_TYPE));
    }

    /// <summary>目录由当前权限定义生成，写接口拒绝未知标识和旧布尔结构。</summary>
    [MongoHttpFact]
    public async Task CatalogAndStrictGrantPayloadsUseTheSamePermissionDefinitions()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var definitions = await fixture.Admin.Client.GetFromJsonAsync<PermissionDefinition[]>("/api/auth/permissions");
        Assert.Equal(PermissionCatalog.Definitions, definitions);
        var clipManagement = Assert.Single(definitions!, definition => definition.Id == PermissionIds.MANAGE_CLIPS);
        Assert.True(clipManagement.IsDangerous);
        Assert.False(clipManagement.DefaultGranted);
        var user = await fixture.CreateUserAsync("strict-permissions@example.test");
        var endpoint = $"/api/admin/users/{user.Id}/permissions";
        foreach (var payload in new object[] { new[] { "unknown.permission" }, new { manageRecordings = true } })
        {
            using var response = await fixture.Admin.SendAsync(HttpMethod.Put, endpoint, payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(PermissionSet.MemberDefaults,
            (await fixture.Context.Collection<UserAccount>("users").Find(account => account.Id == user.Id)
                .SingleAsync()).Permissions);
    }

    /// <summary>HTTP 设置必须显式提交权限数组，缺失或无效输入不能把已撤销的默认权限补回。</summary>
    [MongoHttpFact]
    public async Task DefaultPermissionSettingsRequireExplicitArraysAndPreserveRevocations()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var settings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
        settings!["defaultUserPermissions"] = new JsonArray();
        using (var revoked = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", settings))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }

        var persisted = await File.ReadAllTextAsync(fixture.ConfigFile);
        foreach (var invalid in new[] { "missing", "null", "object", "non-string", "unknown" })
        {
            var request = settings.DeepClone().AsObject();
            if (invalid == "missing")
            {
                request.Remove("defaultUserPermissions");
            }
            else
            {
                request["defaultUserPermissions"] = invalid switch
                {
                    "null" => null,
                    "object" => new JsonObject { ["manageRecordings"] = true },
                    "non-string" => new JsonArray(true),
                    _ => new JsonArray("unknown.permission")
                };
            }

            using var denied = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", request);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal(persisted, await File.ReadAllTextAsync(fixture.ConfigFile));
            var current = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");
            Assert.Empty(current!["defaultUserPermissions"]!.AsArray());
        }
    }

    private static ClaimsPrincipal CookiePrincipal(HttpServerFixture fixture, HttpBrowser browser)
    {
        var options = fixture.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var cookie = browser.Cookies.GetCookies(fixture.Address)[options.Cookie.Name!]!;
        var ticket = options.TicketDataFormat.Unprotect(cookie.Value);
        Assert.NotNull(ticket);
        return ticket.Principal;
    }
}
