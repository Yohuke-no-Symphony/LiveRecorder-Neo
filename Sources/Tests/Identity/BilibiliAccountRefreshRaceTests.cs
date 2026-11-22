using System.Net;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证凭据刷新与录制即时失效交错时的新凭据恢复和旧凭据隔离。</summary>
public sealed class BilibiliAccountRefreshRaceTests
{
    private const string STREAM_PATH = "/xlive/web-room/v2/index/getRoomPlayInfo";

    /// <summary>刷新请求在旧凭据被判失效后返回新 Cookie 时，应立即重新验证并恢复使用。</summary>
    [MongoFact]
    public async Task RotatedCookieIsValidatedImmediatelyAfterConcurrentExpiry()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/correspond/1/", StringComparison.Ordinal))
            {
                return new(HttpStatusCode.OK) { Content = new StringContent("<div id='1-name'>refresh-challenge</div>") };
            }
            switch (path)
            {
                case "/x/passport-login/web/cookie/info":
                    return BilibiliAccountTestFixture.Json("""{"code":0,"data":{"refresh":true,"timestamp":1800000000000}}""");
                case "/x/passport-login/web/cookie/refresh":
                    entered.TrySetResult();
                    await release.Task;
                    return SuccessfulExchange();
                case "/x/passport-login/web/confirm/refresh":
                    return BilibiliAccountTestFixture.Json("""{"code":0,"data":null}""");
                case STREAM_PATH when request.Headers.GetValues("Cookie").Any(cookie => cookie.Contains("original-42", StringComparison.Ordinal)):
                    return BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}""");
                default:
                    return null;
            }
        };
        var maintenance = fixture.MaintainAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000, account.Id));
            Assert.Equal("BILIBILI_ACCOUNT_EXPIRED", error.Code);
            Assert.Equal("Expired", Assert.Single(await fixture.Service.ListAccountsAsync()).Status);
        }
        finally
        {
            release.TrySetResult();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var saved = await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(value => value.Id == account.Id).SingleAsync();
        var credentials = JsonSerializer.Deserialize<BilibiliLoginCredentials>(fixture.Identity.Protector.Unprotect(saved.ProtectedCredentials))!;
        Assert.Contains("SESSDATA=rotated-42", credentials.Cookie);
        Assert.Equal("rotated-refresh-token", credentials.RefreshToken);
        Assert.Empty(credentials.PendingConfirmationToken);
        Assert.NotEqual(account.ProtectedCredentials, saved.ProtectedCredentials);
        Assert.DoesNotContain("rotated-42", saved.ProtectedCredentials);
        Assert.Equal("Valid", saved.Status);
        Assert.Contains(fixture.Requests, request => request.Path == "/x/web-interface/nav" && request.Cookie.Contains("SESSDATA=rotated-42", StringComparison.Ordinal));
        await fixture.Service.ResolveStreamAsync(100, 10000, account.Id);
        Assert.Contains("SESSDATA=rotated-42", fixture.RoomCookies.Last());
    }

    /// <summary>刷新只清理确认令牌而未更换 Cookie 时，不得解除并发认证失败留下的过期状态。</summary>
    [MongoFact]
    public async Task ConfirmationMetadataWithoutCookieRotationCannotRestoreExpiredAccount()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42);
        var original = JsonSerializer.Deserialize<BilibiliLoginCredentials>(fixture.Identity.Protector.Unprotect(account.ProtectedCredentials))!;
        var pending = original with { PendingConfirmationToken = "pending-confirmation" };
        await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").UpdateOneAsync(
            value => value.Id == account.Id,
            Builders<BilibiliAccount>.Update.Set(value => value.ProtectedCredentials, fixture.Identity.Protector.Protect(JsonSerializer.Serialize(pending))));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/x/passport-login/web/confirm/refresh":
                    entered.TrySetResult();
                    await release.Task;
                    return BilibiliAccountTestFixture.Json("""{"code":0,"data":null}""");
                case STREAM_PATH:
                    return BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}""");
                default:
                    return null;
            }
        };
        var maintenance = fixture.MaintainAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000, account.Id));
            Assert.Equal("BILIBILI_ACCOUNT_EXPIRED", error.Code);
        }
        finally
        {
            release.TrySetResult();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var saved = await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(value => value.Id == account.Id).SingleAsync();
        var credentials = JsonSerializer.Deserialize<BilibiliLoginCredentials>(fixture.Identity.Protector.Unprotect(saved.ProtectedCredentials))!;
        Assert.Equal(original.Cookie, credentials.Cookie);
        Assert.Equal("", credentials.PendingConfirmationToken);
        Assert.Equal("Expired", saved.Status);
        Assert.Single(fixture.Requests, request => request.Path == "/x/web-interface/nav");
    }

    [MongoFact]
    public async Task ConfirmationRetriesOnlyAfterCompleteCredentialsArePersisted()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42);
        var confirmationSnapshots = new List<(BilibiliLoginCredentials Credentials, string Cookie, string Body)>();
        fixture.BeforeRequest = async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/correspond/1/", StringComparison.Ordinal))
            {
                return new(HttpStatusCode.OK) { Content = new StringContent("<div id='1-name'>challenge</div>") };
            }
            switch (path)
            {
                case "/x/passport-login/web/cookie/info":
                    return BilibiliAccountTestFixture.Json("""{"code":0,"data":{"refresh":true,"timestamp":1800000000000}}""");
                case "/x/passport-login/web/cookie/refresh":
                    return SuccessfulExchange();
                case "/x/passport-login/web/confirm/refresh":
                    var durable = await ReadCredentialsAsync(fixture, account.Id);
                    confirmationSnapshots.Add((durable, request.Headers.GetValues("Cookie").Single(), await request.Content!.ReadAsStringAsync()));
                    if (confirmationSnapshots.Count == 1)
                    {
                        throw new HttpRequestException("Simulated connection failure.");
                    }
                    return BilibiliAccountTestFixture.Json(confirmationSnapshots.Count == 2 ? """{"data":null}""" : """{"code":0,"data":null}""");
                default:
                    return null;
            }
        };

        await fixture.MaintainAsync();
        Assert.Equal("refresh", (await ReadCredentialsAsync(fixture, account.Id)).PendingConfirmationToken);
        await fixture.MaintainAsync();
        Assert.Equal("refresh", (await ReadCredentialsAsync(fixture, account.Id)).PendingConfirmationToken);
        await fixture.MaintainAsync();
        Assert.Empty((await ReadCredentialsAsync(fixture, account.Id)).PendingConfirmationToken);

        Assert.Single(fixture.Requests, request => request.Path == "/x/passport-login/web/cookie/info");
        Assert.Single(fixture.Requests, request => request.Path == "/x/passport-login/web/cookie/refresh");
        Assert.Equal(3, confirmationSnapshots.Count);
        Assert.All(confirmationSnapshots, snapshot =>
        {
            Assert.Contains("SESSDATA=rotated-42", snapshot.Credentials.Cookie);
            Assert.Contains("bili_jct=rotated-csrf", snapshot.Credentials.Cookie);
            Assert.Equal("rotated-refresh-token", snapshot.Credentials.RefreshToken);
            Assert.Equal("refresh", snapshot.Credentials.PendingConfirmationToken);
            Assert.Contains("SESSDATA=rotated-42", snapshot.Cookie);
            Assert.Equal("csrf=rotated-csrf&refresh_token=refresh", snapshot.Body);
        });
    }

    [MongoFact]
    public async Task DeletionDuringExchangePreventsConfirmationOfUnsavedCredentials()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/correspond/1/", StringComparison.Ordinal))
            {
                return new(HttpStatusCode.OK) { Content = new StringContent("<div id='1-name'>challenge</div>") };
            }
            if (path == "/x/passport-login/web/cookie/info")
            {
                return BilibiliAccountTestFixture.Json("""{"code":0,"data":{"refresh":true,"timestamp":1800000000000}}""");
            }
            if (path == "/x/passport-login/web/cookie/refresh")
            {
                entered.TrySetResult();
                await release.Task;
                return SuccessfulExchange();
            }
            return null;
        };

        var maintenance = fixture.MaintainAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Service.DeleteAccountAsync(account.Id);
        }
        finally
        {
            release.TrySetResult();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(await fixture.Service.ListAccountsAsync());
        Assert.DoesNotContain(fixture.Requests, request => request.Path == "/x/passport-login/web/confirm/refresh");
    }

    private static HttpResponseMessage SuccessfulExchange()
    {
        var response = BilibiliAccountTestFixture.Json("""{"code":0,"data":{"refresh_token":"rotated-refresh-token"}}""");
        response.Headers.TryAddWithoutValidation("Set-Cookie", new[]
        {
            "SESSDATA=rotated-42; Domain=.bilibili.com; Path=/; Secure; HttpOnly",
            "bili_jct=rotated-csrf; Domain=.bilibili.com; Path=/; Secure",
            "DedeUserID=42; Domain=.bilibili.com; Path=/; Secure"
        });
        return response;
    }

    private static async Task<BilibiliLoginCredentials> ReadCredentialsAsync(BilibiliAccountTestFixture fixture, string accountId)
    {
        var saved = await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(account => account.Id == accountId).SingleAsync();
        return JsonSerializer.Deserialize<BilibiliLoginCredentials>(fixture.Identity.Protector.Unprotect(saved.ProtectedCredentials))!;
    }
}
