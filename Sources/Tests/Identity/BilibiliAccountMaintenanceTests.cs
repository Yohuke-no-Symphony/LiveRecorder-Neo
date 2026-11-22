using System.Net;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证后台刷新遇到不完整的平台响应时的告警和重试。</summary>
public sealed class BilibiliAccountMaintenanceTests
{
    [MongoFact]
    public async Task IncompleteRefreshResponsesWarnOnceUntilMaintenanceSucceeds()
    {
        var logger = new BilibiliMaintenanceTestLogger();
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync(logger);
        var account = await fixture.AddAccountAsync(42);
        var otherAccount = await fixture.AddAccountAsync(43);
        const string COOKIE_INFO = "/x/passport-login/web/cookie/info";
        var requiresRefresh = true;
        var hasChallenge = false;
        fixture.BeforeRequest = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == COOKIE_INFO && requiresRefresh && request.Headers.GetValues("Cookie").Single().Contains("SESSDATA=original-42", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(BilibiliAccountTestFixture.Json("""{"code":0,"data":{"refresh":true,"timestamp":1800000000000}}"""));
            }
            if (path.StartsWith("/correspond/1/", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK)
                {
                    Content = new StringContent(hasChallenge ? "<div id='1-name'>challenge</div>" : "<div>unavailable</div>")
                });
            }
            return Task.FromResult<HttpResponseMessage?>(path == "/x/passport-login/web/cookie/refresh"
                ? BilibiliAccountTestFixture.Json("""{"code":0,"data":{}}""")
                : null);
        };

        await fixture.MaintainAsync();
        await fixture.MaintainAsync();

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(account.Id, warning.Message);
        Assert.Contains("自动刷新登录失败", warning.Message);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains(account.Id, StringComparison.Ordinal));
        Assert.Equal(2, fixture.Requests.Count(request => request.Path.StartsWith("/correspond/1/", StringComparison.Ordinal)));
        Assert.Equal(2, fixture.Requests.Count(request => request.Path == COOKIE_INFO && request.Cookie.Contains("SESSDATA=original-43", StringComparison.Ordinal)));
        var saved = await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(value => value.Id == account.Id).SingleAsync();
        Assert.Equal("Valid", saved.Status);
        Assert.Equal(account.ProtectedCredentials, saved.ProtectedCredentials);
        await fixture.Service.ResolveStreamAsync(100, 10000, account.Id);
        Assert.Contains("SESSDATA=original-42", Assert.Single(fixture.RoomCookies));

        requiresRefresh = false;
        await fixture.MaintainAsync();
        requiresRefresh = true;
        hasChallenge = true;
        await fixture.MaintainAsync();

        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, entry => Assert.Contains(account.Id, entry.Message));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(otherAccount.Id, StringComparison.Ordinal));
        Assert.Single(fixture.Requests, request => request.Path == "/x/passport-login/web/cookie/refresh");
    }
}
