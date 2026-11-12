using System.Net;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>使用真实 Kestrel 验证宿主能启动且匿名初始化不依赖数据库。</summary>
public sealed class ServerStartupTests
{
    [Fact]
    public async Task ServerStartsOnDynamicPortAndIssuesAnonymousCsrf()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        using var response = await fixture.Admin.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await fixture.Admin.RefreshCsrfAsync();
        Assert.Null(me["user"]);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Admin.CsrfToken));
    }
}
