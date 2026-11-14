using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证采集账号的后台登录、凭据保护与流解析。</summary>
public sealed class BilibiliAccountIntegrationTests
{
    [MongoFact]
    public async Task ExplicitlyExpiredAccountCannotSilentlyFallBackToAnonymousCapture()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        fixture.Http.Handle = _ => Task.FromResult(Json("""{"code":-101,"message":"未登录"}"""));
        var credentials = new Yohuke.DanmuCore.Models.Accounts.BilibiliLoginCredentials { Cookie = "SESSDATA=expired;bili_jct=csrf;DedeUserID=42", RefreshToken = "old-refresh" };
        var account = new BilibiliAccount { Uid = 42, UserName = "采集账号", ProtectedCredentials = fixture.Protector.Protect(JsonSerializer.Serialize(credentials)) };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        using var service = new BilibiliService(fixture.Context, fixture.Protector, fixture.Http, NullLogger<BilibiliService>.Instance, fixture.Settings, new BilibiliEventConnection(fixture.Http, NullLogger<BilibiliEventConnection>.Instance));
        await (Task)typeof(BilibiliService).GetMethod("MaintainAccountsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [CancellationToken.None])!;
        Assert.Equal("Expired", Assert.Single(await service.ListAccountsAsync()).Status);
        var error = await Assert.ThrowsAsync<Yohuke.LiveRecorder.Application.InfrastructureException>(() => service.ResolveStreamAsync(100, 10000));
        Assert.Equal(409, error.StatusCode);
    }

    [MongoFact]
    public async Task QrBindingStoresProtectedCredentialsAndReturnsOnlyPublicAccountMetadata()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        fixture.Http.Handle = request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/x/passport-login/web/qrcode/generate":
                    return Task.FromResult(Json("""{"code":0,"data":{"url":"https://passport.bilibili.com/scan?key=example","qrcode_key":"example"}}"""));
                case "/x/passport-login/web/qrcode/poll":
                    var success = Json("""{"code":0,"data":{"code":0,"refresh_token":"backend-refresh-token"}}""");
                    success.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "SESSDATA=backend-session; Domain=.bilibili.com; Path=/; Secure", "bili_jct=backend-csrf; Domain=.bilibili.com; Path=/; Secure", "DedeUserID=42; Domain=.bilibili.com; Path=/; Secure" });
                    return Task.FromResult(success);
                case "/x/web-interface/nav":
                    var authenticated = request.Headers.TryGetValues("Cookie", out var cookies)
                                        && cookies.Single().Contains("SESSDATA=backend-session", StringComparison.Ordinal);
                    return Task.FromResult(Json(JsonSerializer.Serialize(new
                    {
                        code = authenticated ? 0 : -101,
                        data = new
                        {
                            isLogin = authenticated,
                            mid = authenticated ? 42 : 0,
                            uname = "主播",
                            wbi_img = new
                            {
                                img_url = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                                sub_url = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"
                            }
                        }
                    })));
                case "/x/web-interface/card":
                    return Task.FromResult(Json("""{"code":0,"data":{"card":{"mid":42,"name":"主播","face":"https://i0.hdslb.com/face.jpg"}}}"""));
                case "/xlive/web-room/v1/index/getInfoByRoom":
                    return Task.FromResult(Json("""{"code":0,"data":{"room_info":{"uid":42,"room_id":100,"title":"直播标题","cover":"https://i0.hdslb.com/cover.jpg","live_status":1,"live_start_time":1800000000},"anchor_info":{"base_info":{"uname":"主播","face":"https://i0.hdslb.com/face.jpg"}}}}"""));
                case "/xlive/web-room/v2/index/getRoomPlayInfo":
                    return Task.FromResult(Json("""{"code":0,"data":{"room_id":100,"uid":42,"live_status":1,"live_time":1800000000,"playurl_info":{"playurl":{"stream":[{"protocol_name":"http_stream","format":[{"format_name":"flv","codec":[{"codec_name":"avc","current_qn":10000,"base_url":"/recording.flv","url_info":[{"host":"https://live.example.test","extra":"?token=stream"}]}]}]}]}}}}"""));
                default:
                    throw new InvalidOperationException("Unexpected endpoint: " + request.RequestUri.AbsolutePath);
            }
        };
        using var service = new BilibiliService(fixture.Context, fixture.Protector, fixture.Http, NullLogger<BilibiliService>.Instance, fixture.Settings, new BilibiliEventConnection(fixture.Http, NullLogger<BilibiliEventConnection>.Instance));
        var qr = await service.BeginQrAsync();
        var completed = await service.PollQrAsync(qr.SessionId);
        Assert.Equal("Complete", completed.Status);
        Assert.Equal(42, completed.Account!.Uid);
        var stored = await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(x => x.Id == completed.Account.Id).SingleAsync();
        Assert.DoesNotContain("backend-session", stored.ProtectedCredentials);
        Assert.Contains("backend-session", fixture.Protector.Unprotect(stored.ProtectedCredentials));
        var json = JsonSerializer.Serialize(await service.ListAccountsAsync());
        Assert.DoesNotContain("backend-session", json);
        Assert.DoesNotContain("backend-refresh-token", json);
        var room = await service.GetRoomAsync(100, completed.Account.Id);
        Assert.Equal("直播标题", room.Title);
        Assert.Equal("https://i0.hdslb.com/cover.jpg", room.CoverUrl);
        var source = await service.ResolveStreamAsync(100, 10000, completed.Account.Id);
        Assert.Equal("https://live.example.test/recording.flv?token=stream", source.Url);
        Assert.Equal(10000, source.Quality);
        Assert.False(source.Headers.ContainsKey("Cookie"));
        await service.DeleteAccountAsync(completed.Account.Id);
        Assert.Empty(await service.ListAccountsAsync());
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
