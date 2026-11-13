using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证直播间搜索的管理员权限、参数校验和无账号响应。</summary>
public sealed class BilibiliRoomSearchHttpTests
{
    [MongoHttpFact]
    public async Task EmptySearchReturnsLoginIssueWithoutExposingCredentials()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);

        using var response = await fixture.Admin.Client.GetAsync("/api/admin/bilibili/rooms/search");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = JsonNode.Parse(text)!;
        Assert.Empty(page["results"]!.AsArray());
        Assert.Equal("followingLoginRequired", page["issues"]![0]!["code"]!.GetValue<string>());
        Assert.False(page["hasMore"]!.GetValue<bool>());
        Assert.DoesNotContain("SESSDATA", text);
        Assert.DoesNotContain("protectedCredentials", text, StringComparison.OrdinalIgnoreCase);
    }

    [MongoHttpFact]
    public async Task SearchRejectsInvalidPageAndUnknownSelectedAccount()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);

        using var invalid = await fixture.Admin.Client.GetAsync("/api/admin/bilibili/rooms/search?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!;
        Assert.Equal("BILIBILI_SEARCH_INVALID", problem["code"]!.GetValue<string>());

        using var missingAccount = await fixture.Admin.Client.GetAsync("/api/admin/bilibili/rooms/search?accountId=missing");
        Assert.Equal(HttpStatusCode.Conflict, missingAccount.StatusCode);
    }

    [MongoHttpFact]
    public async Task SearchRequiresAdministratorForAnonymousAndRegularUsers()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var browser = fixture.NewBrowser();
        using var anonymous = await browser.Client.GetAsync("/api/admin/bilibili/rooms/search");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var user = await fixture.CreateUserAsync("room-search-viewer@example.test");
        await browser.LoginAsync(user.Email, HttpServerFixture.USER_PASSWORD);
        using var regularUser = await browser.Client.GetAsync("/api/admin/bilibili/rooms/search");
        Assert.Equal(HttpStatusCode.Forbidden, regularUser.StatusCode);
    }
}
