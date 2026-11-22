using System.Net;
using System.Text.Json.Nodes;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.DanmuCore.Services;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>平台登录协议的离线合约测试，不提交真实账号或短信。</summary>
public sealed class BilibiliWebLoginTests
{
    [Fact]
    public async Task QrPollingKeepsWaitingScannedAndExpiredDistinct()
    {
        var index = 0;
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("passport.bilibili.com", request.RequestUri!.Host);
            if (index++ == 0)
            {
                Assert.Equal("/x/passport-login/web/qrcode/generate", request.RequestUri.AbsolutePath);
                return Task.FromResult(Json("""{"code":0,"data":{"url":"https://passport.bilibili.com/scan?key=example","qrcode_key":"example"}}"""));
            }
            return Task.FromResult(Json(new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["code"] = new[] { 86101, 86090, 86038 }[index - 2] } }.ToJsonString()));
        });
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        var qr = await client.BeginQrAsync();
        Assert.Equal("example", qr.Key);
        Assert.Equal("Waiting", (await client.PollQrAsync(qr.Key)).Status);
        Assert.Equal("Scanned", (await client.PollQrAsync(qr.Key)).Status);
        Assert.Equal("Expired", (await client.PollQrAsync(qr.Key)).Status);
    }

    [Fact]
    public async Task SuccessfulQrCapturesCookiesAndRefreshTokenOnlyOnBackend()
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(LoginSuccess("qr-session", "csrf-one", "refresh-one")));
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        var result = await client.PollQrAsync("example");
        Assert.Equal("Complete", result.Status);
        Assert.NotNull(result.Credentials);
        Assert.Contains("SESSDATA=qr-session", result.Credentials.Cookie);
        Assert.Contains("bili_jct=csrf-one", result.Credentials.Cookie);
        Assert.Equal("refresh-one", result.Credentials.RefreshToken);
    }

    [Fact]
    public async Task QrCallbackRejectsUntrustedDestinationsBeforeRequestingThem()
    {
        var count = 0;
        using var handler = new StubHttpHandler(_ =>
        {
            count++;
            return Task.FromResult(Json("""{"code":0,"data":{"code":0,"url":"https://127.0.0.1/private"}}"""));
        });
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.PollQrAsync("example"));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("geetest")]
    [InlineData("geetest4")]
    [InlineData("img")]
    public async Task SmsSupportsEachPlatformChallengeAndKeepsItsSessionKey(string type)
    {
        var count = 0;
        using var handler = new StubHttpHandler(async request =>
        {
            count++;
            if (count == 1)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Json(new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["type"] = type, ["token"] = "challenge-token", ["geetest"] = new JsonObject { ["gt"] = "captcha-id", ["challenge"] = "gee-challenge" } } }.ToJsonString());
            }
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Equal("/x/passport-login/web/sms/send", request.RequestUri!.AbsolutePath);
            Assert.Contains("token=challenge-token", body);
            Assert.Contains("tel=13800138000", body);
            if (type == "geetest4")
            {
                Assert.Contains("lot_number=lot", body);
                Assert.Contains("captcha_output=output", body);
                Assert.DoesNotContain("validate=", body);
            }
            else if (type == "geetest")
            {
                Assert.Contains("validate=validated", body);
                Assert.Contains("seccode=secure", body);
                Assert.DoesNotContain("lot_number=", body);
            }
            else
            {
                Assert.Contains("captcha=ABCDE", body);
            }
            return Json("""{"code":0,"data":{"captcha_key":"sms-session-key"}}""");
        });
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        var challenge = await client.GetChallengeAsync();
        var proof = new BilibiliChallengeProof { Token = challenge.Token, Challenge = "gee-challenge", Validate = "validated", Seccode = "secure", LotNumber = "lot", CaptchaOutput = "output", PassToken = "pass", GenTime = "1", Captcha = "ABCDE" };
        Assert.Equal("sms-session-key", await client.SendSmsAsync("86", "13800138000", challenge, proof));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SmsCannotUseAnotherChallengeToken()
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No HTTP request was expected."));
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendSmsAsync("86", "13800138000", new("geetest", "expected", "gt", "challenge", null), new() { Token = "different" }));
    }

    [Fact]
    public async Task SmsSafetyVerificationDoesNotProduceCredentials()
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(Json("""{"code":0,"data":{"status":2,"url":"https://passport.bilibili.com/safety/verify"}}""")));
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        var result = await client.CompleteSmsAsync("86", "13800138000", "captcha-key", "123456");
        Assert.Equal("VerificationRequired", result.Status);
        Assert.Null(result.Credentials);
        Assert.Equal("https://passport.bilibili.com/safety/verify", result.VerificationUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshPersistsRotatedCredentialsEvenWhenConfirmationFails(bool confirmationFails)
    {
        var index = 0;
        using var handler = new StubHttpHandler(async request =>
        {
            switch (index++)
            {
                case 0:
                    Assert.Equal("/x/passport-login/web/cookie/info", request.RequestUri!.AbsolutePath);
                    Assert.Contains("SESSDATA=previous-session", request.Headers.GetValues("Cookie").Single());
                    return Json("""{"code":0,"data":{"refresh":true,"timestamp":1800000000000}}""");
                case 1:
                    Assert.StartsWith("/correspond/1/", request.RequestUri!.AbsolutePath);
                    Assert.Equal(256, request.RequestUri.AbsolutePath.Split('/').Last().Length);
                    return new(HttpStatusCode.OK) { Content = new StringContent("<div id='1-name'>refresh-challenge</div>") };
                case 2:
                    Assert.Contains("refresh_csrf=refresh-challenge", await request.Content!.ReadAsStringAsync());
                    return LoginSuccess("next-session", "csrf-next", "refresh-next");
                case 3:
                    Assert.Equal("/x/passport-login/web/confirm/refresh", request.RequestUri!.AbsolutePath);
                    Assert.Contains("csrf=csrf-next", await request.Content!.ReadAsStringAsync());
                    if (confirmationFails)
                    {
                        throw new HttpRequestException("Simulated connection failure.");
                    }
                    return Json("""{"code":0,"data":null}""");
                default:
                    throw new InvalidOperationException("Unexpected request.");
            }
        });
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        var result = await client.RefreshAsync(new() { Cookie = "SESSDATA=previous-session;bili_jct=csrf-old;DedeUserID=42", RefreshToken = "refresh-old" });
        Assert.Contains("SESSDATA=next-session", result.Cookie);
        Assert.Equal("refresh-next", result.RefreshToken);
        Assert.Equal(confirmationFails ? "refresh-old" : "", result.PendingConfirmationToken);
    }

    [Fact]
    public async Task NoRefreshLeavesExistingSessionIntact()
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(Json("""{"code":0,"data":{"refresh":false}}""")));
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);
        var result = await client.RefreshAsync(new() { Cookie = "SESSDATA=existing;bili_jct=csrf", RefreshToken = "refresh-old" });
        Assert.Contains("SESSDATA=existing", result.Cookie);
        Assert.Equal("refresh-old", result.RefreshToken);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("new-session", null)]
    [InlineData(null, "new-csrf")]
    public async Task RefreshRejectsMissingCookiesFromExchangeResponse(string? newSession, string? newCsrf)
    {
        var paths = new List<string>();
        using var handler = new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/x/passport-login/web/cookie/info")
            {
                return Task.FromResult(Json("""{"code":0,"data":{"refresh":true,"timestamp":1800000000000}}"""));
            }
            if (path.StartsWith("/correspond/1/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div id='1-name'>challenge</div>") });
            }
            Assert.Equal("/x/passport-login/web/cookie/refresh", path);
            var response = Json("""{"code":0,"data":{"refresh_token":"new-refresh-token"}}""");
            if (newSession is not null)
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", $"SESSDATA={newSession}; Domain=.bilibili.com; Path=/; Secure");
            }
            if (newCsrf is not null)
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", $"bili_jct={newCsrf}; Domain=.bilibili.com; Path=/; Secure");
            }
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new BilibiliWebLoginClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(new()
        {
            Cookie = "SESSDATA=old-session;bili_jct=old-csrf",
            RefreshToken = "old-refresh-token"
        }));
        Assert.Equal(3, paths.Count);
        Assert.DoesNotContain("/x/passport-login/web/confirm/refresh", paths);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage LoginSuccess(string session, string csrf, string refresh)
    {
        var response = Json(new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["code"] = 0, ["status"] = 0, ["refresh_token"] = refresh } }.ToJsonString());
        response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { $"SESSDATA={session}; Domain=.bilibili.com; Path=/; Secure; HttpOnly", $"bili_jct={csrf}; Domain=.bilibili.com; Path=/; Secure", "DedeUserID=42; Domain=.bilibili.com; Path=/; Secure" });
        return response;
    }
}
