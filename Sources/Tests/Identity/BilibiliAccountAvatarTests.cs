using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证账号头像的查询回退、历史资料补齐和并发保护。</summary>
public sealed class BilibiliAccountAvatarTests
{
    [MongoFact]
    public async Task MaintenanceBackfillsMissingAvatarAndNormalizesHttps()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var account = await AddAccountAsync(fixture);
        fixture.Http.Handle = request => Task.FromResult(Respond(request, "//i0.hdslb.com/account.jpg"));
        using var service = CreateService(fixture);

        await MaintainAsync(service);

        var summary = Assert.Single(await service.ListAccountsAsync());
        Assert.Equal(account.Id, summary.Id);
        Assert.Equal("Valid", summary.Status);
        Assert.Equal("https://i0.hdslb.com/account.jpg", summary.AvatarUrl);
        Assert.NotNull(summary.LastValidatedAtUtc);
    }

    [MongoFact]
    public async Task MaintenanceRetriesMissingAvatarAfterFailureAndStillRefreshesCredentials()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await AddAccountAsync(fixture);
        var avatar = "";
        var refreshes = 0;
        fixture.Http.Handle = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/passport-login/web/cookie/info")
            {
                refreshes++;
            }
            return Task.FromResult(Respond(request, avatar, cardUnavailable: true));
        };
        using var service = CreateService(fixture);

        await MaintainAsync(service);
        var unresolved = Assert.Single(await service.ListAccountsAsync());
        Assert.Equal("Valid", unresolved.Status);
        Assert.Equal("", unresolved.AvatarUrl);
        Assert.Equal(1, refreshes);

        avatar = "https://i1.hdslb.com/recovered.jpg";
        await MaintainAsync(service);
        Assert.Equal(avatar, Assert.Single(await service.ListAccountsAsync()).AvatarUrl);
        Assert.Equal(2, refreshes);
    }

    [MongoFact]
    public async Task MaintenanceDoesNotLookupOrOverwriteExistingAvatar()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var account = await AddAccountAsync(fixture, "https://i0.hdslb.com/existing.jpg");
        var avatarLookups = 0;
        fixture.Http.Handle = request =>
        {
            if (request.RequestUri!.AbsolutePath is "/x/web-interface/card" or "/x/space/wbi/acc/info")
            {
                avatarLookups++;
            }
            return Task.FromResult(Respond(request));
        };
        using var service = CreateService(fixture);

        await MaintainAsync(service);

        Assert.Equal(account.AvatarUrl, Assert.Single(await service.ListAccountsAsync()).AvatarUrl);
        Assert.Equal(0, avatarLookups);
    }

    [MongoFact]
    public async Task MaintenanceDoesNotOverwriteAvatarSavedDuringLookup()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var account = await AddAccountAsync(fixture);
        const string CURRENT_AVATAR = "https://i1.hdslb.com/current.jpg";
        fixture.Http.Handle = async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/card")
            {
                await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").UpdateOneAsync(
                    x => x.Id == account.Id, Builders<BilibiliAccount>.Update.Set(x => x.AvatarUrl, CURRENT_AVATAR));
            }
            return Respond(request, "https://i0.hdslb.com/stale.jpg");
        };
        using var service = CreateService(fixture);

        await MaintainAsync(service);

        Assert.Equal(CURRENT_AVATAR, Assert.Single(await service.ListAccountsAsync()).AvatarUrl);
    }

    [MongoFact]
    public async Task QrBindingUsesSignedFallbackWhenCardLookupFails()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        fixture.Http.Handle = request => Task.FromResult(Respond(request, "http://i0.hdslb.com/fallback.jpg", cardUnavailable: true));
        using var service = CreateService(fixture);

        var qr = await service.BeginQrAsync();
        var result = await service.PollQrAsync(qr.SessionId);

        Assert.Equal("Complete", result.Status);
        Assert.Equal("https://i0.hdslb.com/fallback.jpg", result.Account!.AvatarUrl);
        Assert.Equal(result.Account.AvatarUrl, Assert.Single(await service.ListAccountsAsync()).AvatarUrl);
    }

    [MongoFact]
    public async Task QrBindingPreservesStoredAvatarWhenBothLookupsFail()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var account = await AddAccountAsync(fixture, "https://i0.hdslb.com/existing.jpg");
        fixture.Http.Handle = request => Task.FromResult(Respond(request, cardUnavailable: true));
        using var service = CreateService(fixture);

        var qr = await service.BeginQrAsync();
        var result = await service.PollQrAsync(qr.SessionId);

        Assert.Equal("Complete", result.Status);
        Assert.Equal(account.Id, result.Account!.Id);
        Assert.Equal(account.AvatarUrl, result.Account.AvatarUrl);
        Assert.Equal(account.AvatarUrl, Assert.Single(await service.ListAccountsAsync()).AvatarUrl);
    }

    [MongoFact]
    public async Task CancellingMissingAvatarLookupStopsMaintenanceWithoutSavingIt()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await AddAccountAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        fixture.Http.Handle = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/card")
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
            }
            return Task.FromResult(Respond(request));
        };
        using var service = CreateService(fixture);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MaintainAsync(service, cancellation.Token));

        Assert.Equal("", Assert.Single(await service.ListAccountsAsync()).AvatarUrl);
    }

    private static BilibiliService CreateService(IdentityFixture fixture) => new(fixture.Context, fixture.Protector, fixture.Http, NullLogger<BilibiliService>.Instance, fixture.Settings, new BilibiliEventConnection(fixture.Http, NullLogger<BilibiliEventConnection>.Instance));

    private static Task MaintainAsync(BilibiliService service, CancellationToken cancellationToken = default) =>
        (Task)typeof(BilibiliService).GetMethod("MaintainAccountsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [cancellationToken])!;

    private static async Task<BilibiliAccount> AddAccountAsync(IdentityFixture fixture, string avatar = "")
    {
        var credentials = new BilibiliLoginCredentials { Cookie = "SESSDATA=account-session; bili_jct=csrf; DedeUserID=42", RefreshToken = "refresh" };
        var account = new BilibiliAccount { Uid = 42, UserName = "采集账号", AvatarUrl = avatar, ProtectedCredentials = fixture.Protector.Protect(JsonSerializer.Serialize(credentials)) };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        return account;
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, string avatar = "", bool cardUnavailable = false)
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/x/web-interface/nav":
                return Json("""{"code":0,"data":{"isLogin":true,"mid":42,"uname":"采集账号","wbi_img":{"img_url":"https://i0.hdslb.com/0123456789abcdef0123456789abcdef.png","sub_url":"https://i0.hdslb.com/fedcba9876543210fedcba9876543210.png"}}}""");
            case "/x/web-interface/card":
                return cardUnavailable
                    ? Json("""{"code":-400,"data":{"card":{"face":"https://i0.hdslb.com/rejected.jpg"}}}""")
                    : Json(JsonSerializer.Serialize(new { code = 0, data = new { card = new { face = avatar } } }));
            case "/x/space/wbi/acc/info":
                return Json(JsonSerializer.Serialize(new { code = 0, data = new { face = avatar } }));
            case "/x/passport-login/web/cookie/info":
                return Json("""{"code":0,"data":{"refresh":false}}""");
            case "/x/passport-login/web/qrcode/generate":
                return Json("""{"code":0,"data":{"url":"https://passport.bilibili.com/scan?key=avatar-test","qrcode_key":"avatar-test"}}""");
            case "/x/passport-login/web/qrcode/poll":
                var response = Json("""{"code":0,"data":{"code":0,"refresh_token":"refresh"}}""");
                response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "SESSDATA=account-session; Domain=.bilibili.com; Path=/; Secure", "bili_jct=csrf; Domain=.bilibili.com; Path=/; Secure", "DedeUserID=42; Domain=.bilibili.com; Path=/; Secure" });
                return response;
            default:
                throw new InvalidOperationException("Unexpected endpoint: " + request.RequestUri.AbsolutePath);
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
