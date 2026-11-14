using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class BilibiliAccountTestFixture : IAsyncDisposable
{
    internal IdentityFixture Identity { get; }
    internal ControlledBilibiliEventConnection Connection { get; } = new();
    internal BilibiliService Service { get; }
    internal HashSet<long> ExpiredUids { get; } = [];
    internal HashSet<long> UnavailableUids { get; } = [];
    internal ConcurrentQueue<(string Path, string Cookie)> Requests { get; } = new();
    internal ConcurrentQueue<string> RoomCookies { get; } = new();
    internal long LoginUid { get; set; } = 42;
    internal long RoomLiveStartTime { get; set; } = 1800000000;
    internal Func<HttpRequestMessage, Task<HttpResponseMessage?>>? BeforeRequest { get; set; }

    private BilibiliAccountTestFixture(IdentityFixture identity)
    {
        Identity = identity;
        Service = new(identity.Context, identity.Protector, identity.Http, NullLogger<BilibiliService>.Instance, identity.Settings, Connection);
        Identity.Http.Handle = RespondAsync;
    }

    internal static async Task<BilibiliAccountTestFixture> CreateAsync() => new(await IdentityFixture.CreateAsync());

    internal async Task<BilibiliAccount> AddAccountAsync(long uid, string status = "Valid")
    {
        var credentials = new BilibiliLoginCredentials { Cookie = ApiCookieHelper.CreateBilibiliCookie($"SESSDATA=original-{uid};bili_jct=csrf;DedeUserID={uid};buvid3=device"), RefreshToken = "refresh" };
        var account = new BilibiliAccount
        {
            Uid = uid,
            UserName = $"账号 {uid}",
            AvatarUrl = "https://i0.hdslb.com/face.jpg",
            ProtectedCredentials = Identity.Protector.Protect(JsonSerializer.Serialize(credentials)),
            Status = status,
            CreatedAtUtc = DateTime.UnixEpoch.AddSeconds(uid)
        };
        await Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        return account;
    }

    internal Task MaintainAsync() => (Task)typeof(BilibiliService).GetMethod("MaintainAccountsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(Service, [CancellationToken.None])!;

    internal static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request)
    {
        var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : "";
        Requests.Enqueue((request.RequestUri!.AbsolutePath, cookie));
        if (request.RequestUri.AbsolutePath == "/xlive/web-room/v2/index/getRoomPlayInfo")
        {
            RoomCookies.Enqueue(cookie);
        }
        if (BeforeRequest is not null && await BeforeRequest(request) is { } response)
        {
            return response;
        }
        var uidText = ApiCookieHelper.GetValue(cookie, "DedeUserID");
        var uid = long.TryParse(uidText, out var parsed) ? parsed : LoginUid;
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/x/passport-login/web/qrcode/generate":
                return Json("""{"code":0,"data":{"url":"https://passport.bilibili.com/scan?key=test","qrcode_key":"test"}}""");
            case "/x/passport-login/web/qrcode/poll":
            case "/x/passport-login/web/login/sms":
                var success = Json("""{"code":0,"data":{"code":0,"status":0,"refresh_token":"refreshed"}}""");
                success.Headers.TryAddWithoutValidation("Set-Cookie", new[]
                {
                    $"SESSDATA=renewed-{LoginUid}; Domain=.bilibili.com; Path=/; Secure",
                    "bili_jct=renewed-csrf; Domain=.bilibili.com; Path=/; Secure",
                    $"DedeUserID={LoginUid}; Domain=.bilibili.com; Path=/; Secure"
                });
                return success;
            case "/x/passport-login/captcha":
                return Json("""{"code":0,"data":{"type":"img","token":"challenge-token","image":"https://passport.bilibili.com/captcha.png"}}""");
            case "/x/passport-login/web/sms/send":
                return Json("""{"code":0,"data":{"captcha_key":"sms-key"}}""");
            case "/x/web-interface/nav":
                if (UnavailableUids.Contains(uid))
                {
                    return Json("""{"code":-412,"message":"temporarily unavailable"}""");
                }
                return ExpiredUids.Contains(uid) && !cookie.Contains("renewed-", StringComparison.Ordinal)
                    ? Json("""{"code":-101,"message":"expired"}""")
                    : Json(JsonSerializer.Serialize(new { code = 0, data = new { isLogin = true, mid = uid, uname = $"重新登录 {uid}", wbi_img = new { img_url = "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png", sub_url = "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png" } } }));
            case "/x/web-interface/card":
                return Json(JsonSerializer.Serialize(new { code = 0, data = new { card = new { mid = uid, name = $"账号 {uid}", face = "https://i0.hdslb.com/face.jpg" } } }));
            case "/x/passport-login/web/cookie/info":
                return Json("""{"code":0,"data":{"refresh":false}}""");
            case "/xlive/web-room/v1/index/getInfoByRoom":
                return Json(JsonSerializer.Serialize(new
                {
                    code = 0,
                    data = new
                    {
                        room_info = new { uid = 1000, room_id = 100, title = "测试直播", live_status = 1, live_start_time = RoomLiveStartTime },
                        anchor_info = new { base_info = new { uname = "测试主播" } }
                    }
                }));
            case "/xlive/web-room/v2/index/getRoomPlayInfo":
                return Json("""{"code":0,"data":{"room_id":100,"live_status":1,"live_time":1800000000,"playurl_info":{"playurl":{"stream":[{"protocol_name":"http_stream","format":[{"format_name":"flv","codec":[{"codec_name":"avc","current_qn":10000,"base_url":"/recording.flv","url_info":[{"host":"https://capture.example.test","extra":""}]}]}]}]}}}}""");
            default:
                throw new InvalidOperationException("Unexpected endpoint: " + request.RequestUri.AbsolutePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Service.Dispose();
        await Identity.DisposeAsync();
    }
}
