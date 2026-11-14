using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证账号管理路由的授权、默认选择及设置保存兼容性。</summary>
public sealed class BilibiliAccountManagementHttpTests
{
    /// <summary>匿名和普通用户不能更改默认账号或发起账号重新登录。</summary>
    [MongoHttpFact]
    public async Task AccountMutationsRequireAdministrator()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var browser = fixture.NewBrowser();
        await browser.RefreshCsrfAsync();
        foreach (var authenticated in new[] { false, true })
        {
            if (authenticated)
            {
                var user = await fixture.CreateUserAsync("bilibili-management@example.test");
                await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
            }
            using var selection = await browser.SendAsync(HttpMethod.Put, "/api/admin/bilibili/accounts/default", new { accountId = "account" });
            using var qr = await browser.SendAsync(HttpMethod.Post, "/api/admin/bilibili/accounts/account/qr");
            using var sms = await browser.SendAsync(HttpMethod.Post, "/api/admin/bilibili/accounts/account/sms", new { cid = "86", tel = "13800138000" });
            var expected = authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            Assert.Equal(expected, selection.StatusCode);
            Assert.Equal(expected, qr.StatusCode);
            Assert.Equal(expected, sms.StatusCode);
        }
    }

    /// <summary>旧设置页提交保留账号页的新默认选择，删除后返回自动选择。</summary>
    [MongoHttpFact]
    public async Task StaleSettingsSaveCannotOverwriteDefaultAccountSelection()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        var first = new BilibiliAccount { Uid = 1, CreatedAtUtc = DateTime.UnixEpoch, UserName = "First", ProtectedCredentials = "private-credentials" };
        var preferred = new BilibiliAccount { Uid = 2, CreatedAtUtc = DateTime.UnixEpoch.AddSeconds(1), UserName = "Preferred", ProtectedCredentials = "private-credentials" };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertManyAsync([first, preferred]);
        var oldSettings = await fixture.Admin.Client.GetFromJsonAsync<JsonObject>("/api/settings");

        using var selected = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/admin/bilibili/accounts/default", new { accountId = preferred.Id });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var response = await selected.Content.ReadAsStringAsync();
        var accounts = JsonNode.Parse(response)!.AsArray();
        Assert.True(accounts.Single(account => account!["id"]!.GetValue<string>() == preferred.Id)!["isDefault"]!.GetValue<bool>());
        Assert.DoesNotContain("private-credentials", response);
        Assert.DoesNotContain("protectedCredentials", response, StringComparison.OrdinalIgnoreCase);

        oldSettings!["defaultBilibiliAccountId"] = first.Id;
        oldSettings["quality"] = 400;
        using var saved = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/settings", oldSettings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var settings = await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync();
        Assert.Equal(preferred.Id, settings.DefaultBilibiliAccountId);
        Assert.Equal(400, settings.Quality);

        using var deleted = await fixture.Admin.SendAsync(HttpMethod.Delete, $"/api/admin/bilibili/accounts/{preferred.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null((await fixture.App.Services.GetRequiredService<ISettingsStore>().GetAsync()).DefaultBilibiliAccountId);
        accounts = (await fixture.Admin.Client.GetFromJsonAsync<JsonArray>("/api/admin/bilibili/accounts"))!;
        var remaining = Assert.Single(accounts)!;
        Assert.Equal(first.Id, remaining["id"]!.GetValue<string>());
        Assert.False(remaining["isDefault"]!.GetValue<bool>());
        Assert.True(remaining["isEffectiveDefault"]!.GetValue<bool>());
    }

    /// <summary>未知目标在请求第三方之前被拒绝，失效账号不能成为新默认。</summary>
    [MongoHttpFact]
    public async Task MissingReloginTargetsAndExpiredDefaultAreRejected()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
        using var qr = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/admin/bilibili/accounts/missing/qr");
        using var sms = await fixture.Admin.SendAsync(HttpMethod.Post, "/api/admin/bilibili/accounts/missing/sms", new { cid = "86", tel = "13800138000" });
        Assert.Equal(HttpStatusCode.Conflict, qr.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sms.StatusCode);
        var expired = new BilibiliAccount { Uid = 3, Status = "Expired" };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(expired);
        using var selection = await fixture.Admin.SendAsync(HttpMethod.Put, "/api/admin/bilibili/accounts/default", new { accountId = expired.Id });
        Assert.Equal(HttpStatusCode.Conflict, selection.StatusCode);
        var problem = await selection.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("BILIBILI_ACCOUNT_EXPIRED", problem!["code"]!.GetValue<string>());
    }
}
