using System.Net;
using System.Text.Json;
using MongoDB.Driver;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证录制 HTTP 请求逐个尝试有效账号，并隔离凭据状态与暂时故障。</summary>
public sealed class BilibiliRecordingAccountRetryTests
{
    private const string ROOM_PATH = "/xlive/web-room/v1/index/getInfoByRoom";
    private const string STREAM_PATH = "/xlive/web-room/v2/index/getRoomPlayInfo";

    /// <summary>取流按显式首选、全局默认和剩余有效账号顺序重试，不修改偏好。</summary>
    [MongoFact]
    public async Task StreamRetriesPreferredDefaultAndRemainingValidAccountsInOrder()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var defaultAccount = await fixture.AddAccountAsync(2);
        var preferred = await fixture.AddAccountAsync(3);
        await fixture.AddAccountAsync(4, "Expired");
        await fixture.AddAccountAsync(5, "Unavailable");
        await fixture.Service.SetDefaultAccountAsync(defaultAccount.Id);
        fixture.BeforeRequest = request => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath == STREAM_PATH && RequestUid(request) != 1
                ? BilibiliAccountTestFixture.Json("""{"code":-412,"message":"temporarily unavailable"}""")
                : null);

        var stream = await fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);

        Assert.Equal("https://capture.example.test/recording.flv", stream.Url);
        Assert.Equal([3L, 2L, 1L], RequestUids(fixture, STREAM_PATH));
        Assert.Equal(defaultAccount.Id, (await fixture.Identity.Settings.GetAsync()).DefaultBilibiliAccountId);
        Assert.All((await fixture.Service.ListAccountsAsync()).Where(account => account.Uid <= 3), account => Assert.Equal("Valid", account.Status));
    }

    /// <summary>房间查询遇到明确认证失败立即标记旧凭据，并继续尝试其他有效账号。</summary>
    [MongoFact]
    public async Task RoomAuthenticationFailureExpiresOnlyFailedAccountAndContinues()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var defaultAccount = await fixture.AddAccountAsync(2);
        var preferred = await fixture.AddAccountAsync(3);
        await fixture.Service.SetDefaultAccountAsync(defaultAccount.Id);
        fixture.BeforeRequest = request => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath != ROOM_PATH ? null : RequestUid(request) switch
            {
                3 => BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}"""),
                2 => new(HttpStatusCode.ServiceUnavailable),
                _ => null
            });

        var room = await fixture.Service.GetRoomAsync(100, preferred.Id);

        Assert.True(room.IsLive);
        Assert.Equal([3L, 2L, 1L], RequestUids(fixture, ROOM_PATH));
        var accounts = await fixture.Service.ListAccountsAsync();
        Assert.Equal("Expired", accounts.Single(account => account.Id == preferred.Id).Status);
        Assert.Equal("Valid", accounts.Single(account => account.Id == defaultAccount.Id).Status);
    }

    /// <summary>取流认证失败无需等待维护轮询即可标记过期并选择下一账号。</summary>
    [MongoFact]
    public async Task StreamAuthenticationFailureImmediatelyExpiresCredentialsAndFallsBack()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        var fallback = await fixture.AddAccountAsync(2);
        fixture.BeforeRequest = request => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath == STREAM_PATH && RequestUid(request) == 1
                ? BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}""")
                : null);

        await fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);

        Assert.Equal([1L, 2L], RequestUids(fixture, STREAM_PATH));
        var accounts = await fixture.Service.ListAccountsAsync();
        Assert.Equal("Expired", accounts.Single(account => account.Id == preferred.Id).Status);
        Assert.Equal(fallback.Id, Assert.Single(accounts, account => account.IsEffectiveDefault).Id);
    }

    /// <summary>显式首选不存在或已经失效时，使用全局默认而不是终止录制。</summary>
    [MongoFact]
    public async Task MissingAndExpiredPreferencesSkipToValidDefault()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var expired = await fixture.AddAccountAsync(2, "Expired");
        var preferred = await fixture.AddAccountAsync(3);
        await fixture.Service.SetDefaultAccountAsync(preferred.Id);

        await fixture.Service.ResolveStreamAsync(100, 10000, "missing-account");
        await fixture.Service.ResolveStreamAsync(100, 10000, expired.Id);

        Assert.Equal([3L, 3L], RequestUids(fixture, STREAM_PATH));
    }

    /// <summary>风控、超时与坏响应只排除本轮账号，每个账号最多请求一次且不会失效。</summary>
    [MongoFact]
    public async Task TemporaryFailuresTryEachCandidateOnceWithoutExpiringCredentials()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        await fixture.AddAccountAsync(2);
        await fixture.AddAccountAsync(3);
        await fixture.Service.SetDefaultAccountAsync(preferred.Id);
        fixture.BeforeRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath != STREAM_PATH)
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }
            return RequestUid(request) switch
            {
                1 => Task.FromResult<HttpResponseMessage?>(BilibiliAccountTestFixture.Json("""{"code":-412}""")),
                2 => Task.FromException<HttpResponseMessage?>(new TaskCanceledException("upstream timeout")),
                _ => Task.FromResult<HttpResponseMessage?>(BilibiliAccountTestFixture.Json("not json"))
            };
        };

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id));

        Assert.Equal([1L, 2L, 3L], RequestUids(fixture, STREAM_PATH));
        Assert.All(await fixture.Service.ListAccountsAsync(), account => Assert.Equal("Valid", account.Status));
    }

    /// <summary>调用方取消第一项请求后不得尝试备用账号。</summary>
    [MongoFact]
    public async Task CallerCancellationStopsBeforeNextCandidate()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        await fixture.AddAccountAsync(2);
        using var cancellation = new CancellationTokenSource();
        fixture.BeforeRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath == STREAM_PATH)
            {
                cancellation.Cancel();
                return Task.FromException<HttpResponseMessage?>(new OperationCanceledException(cancellation.Token));
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id, cancellation.Token));

        Assert.Equal([1L], RequestUids(fixture, STREAM_PATH));
        Assert.All(await fixture.Service.ListAccountsAsync(), account => Assert.Equal("Valid", account.Status));
    }

    /// <summary>补查开播时间时出现认证失败，也必须换号重做完整房间查询。</summary>
    [MongoFact]
    public async Task SupplementaryPlayInfoAuthenticationFailureRetriesEntireRoomQuery()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        await fixture.AddAccountAsync(2);
        fixture.RoomLiveStartTime = 0;
        fixture.BeforeRequest = request => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath == STREAM_PATH && RequestUid(request) == 1
                ? BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}""")
                : null);

        var room = await fixture.Service.GetRoomAsync(100, preferred.Id);

        Assert.True(room.IsLive);
        Assert.Equal([1L, 2L], RequestUids(fixture, ROOM_PATH));
        Assert.Equal([1L, 2L], RequestUids(fixture, STREAM_PATH));
        Assert.Equal("Expired", (await fixture.Service.ListAccountsAsync()).Single(account => account.Id == preferred.Id).Status);
    }

    /// <summary>旧取流请求晚于重登返回失效时，不覆盖新凭据，也不打断新连接。</summary>
    [MongoFact]
    public async Task StaleAuthenticationFailureCannotExpireReloggedCredentials()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(42);
        await fixture.AddAccountAsync(43);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            if (request.RequestUri!.AbsolutePath == STREAM_PATH && RequestUid(request) == 42)
            {
                entered.TrySetResult();
                await release.Task;
                return BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}""");
            }
            return null;
        };
        var stream = fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);
        using var capture = new CancellationTokenSource();
        Task? subscription = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var login = await fixture.Service.BeginAccountQrAsync(preferred.Id);
            await fixture.Service.PollQrAsync(login.SessionId);
            subscription = fixture.Service.SubscribeEventsAsync(100, preferred.Id, (_, _) => Task.CompletedTask, capture.Token);
            var current = await fixture.Connection.NextAsync();
            release.TrySetResult();
            await stream;

            Assert.Contains("renewed-42", current.Cookie);
            Assert.False(current.CredentialsChanged.IsCancellationRequested);
            Assert.Equal("Valid", (await fixture.Service.ListAccountsAsync()).Single(account => account.Id == preferred.Id).Status);
            Assert.Equal([42L, 43L], RequestUids(fixture, STREAM_PATH));
        }
        finally
        {
            release.TrySetResult();
            await capture.CancelAsync();
            if (subscription is not null)
            {
                await subscription.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await stream.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>排队候选在轮到它之前重新登录时，应读取新凭据而不是请求开始时的快照。</summary>
    [MongoFact]
    public async Task CandidateReadsLatestCredentialsWhenItsTurnStarts()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        var fallback = await fixture.AddAccountAsync(42);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            if (request.RequestUri!.AbsolutePath == STREAM_PATH && RequestUid(request) == 1)
            {
                entered.TrySetResult();
                await release.Task;
                return new(HttpStatusCode.ServiceUnavailable);
            }
            return null;
        };
        var stream = fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var login = await fixture.Service.BeginAccountQrAsync(fallback.Id);
            await fixture.Service.PollQrAsync(login.SessionId);
        }
        finally
        {
            release.TrySetResult();
        }

        await stream.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([1L, 42L], RequestUids(fixture, STREAM_PATH));
        Assert.Contains("renewed-42", fixture.RoomCookies.Last());
    }

    /// <summary>已经确认失效的账号遇到暂时无法校验时，仍不得重新参与录制。</summary>
    [MongoFact]
    public async Task UnavailableValidationDoesNotReviveExpiredCredentials()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(1);
        fixture.ExpiredUids.Add(1);
        await fixture.MaintainAsync();
        Assert.Equal("Expired", Assert.Single(await fixture.Service.ListAccountsAsync()).Status);
        fixture.UnavailableUids.Add(1);

        await fixture.MaintainAsync();

        Assert.Equal("Expired", Assert.Single(await fixture.Service.ListAccountsAsync()).Status);
        await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000, account.Id));
        Assert.Empty(RequestUids(fixture, STREAM_PATH));
    }

    /// <summary>同一凭据的旧有效校验晚于实时认证失败返回时，不能覆盖已经确认的过期状态。</summary>
    [MongoFact]
    public async Task StaleValidMaintenanceCannotUndoImmediateAuthenticationFailure()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        var fallback = await fixture.AddAccountAsync(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is not "/x/web-interface/nav" and not STREAM_PATH || RequestUid(request) != 1)
            {
                return null;
            }
            if (path == "/x/web-interface/nav")
            {
                entered.TrySetResult();
                await release.Task;
                return BilibiliAccountTestFixture.Json("""{"code":0,"data":{"isLogin":true,"mid":1,"uname":"旧校验结果"}}""");
            }
            return path == STREAM_PATH
                ? BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}""")
                : null;
        };
        var maintenance = fixture.MaintainAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);
            Assert.Equal("Expired", (await fixture.Service.ListAccountsAsync()).Single(account => account.Id == preferred.Id).Status);
        }
        finally
        {
            release.TrySetResult();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var accounts = await fixture.Service.ListAccountsAsync();
        Assert.Equal("Expired", accounts.Single(account => account.Id == preferred.Id).Status);
        Assert.Equal(fallback.Id, Assert.Single(accounts, account => account.IsEffectiveDefault).Id);
        Assert.Equal([1L, 2L], RequestUids(fixture, STREAM_PATH));
    }

    /// <summary>损坏的加密凭据不会阻止正常账号替补，最终错误不得包含原始凭据内容。</summary>
    [MongoFact]
    public async Task CorruptedCredentialsSkipToFallbackAndNeverAppearInErrors()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        var fallback = await fixture.AddAccountAsync(2);
        const string CORRUPTED_CREDENTIALS = "SESSDATA=corrupted-test-secret;bili_jct=corrupted-test-csrf";
        await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").UpdateOneAsync(
            account => account.Id == preferred.Id,
            Builders<BilibiliAccount>.Update.Set(account => account.ProtectedCredentials, CORRUPTED_CREDENTIALS));

        await fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);

        Assert.Equal([2L], RequestUids(fixture, STREAM_PATH));
        await fixture.Service.DeleteAccountAsync(fallback.Id);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id));
        Assert.Equal("BILIBILI_CREDENTIALS_CORRUPT", error.Code);
        Assert.DoesNotContain(CORRUPTED_CREDENTIALS, error.ToString());
        Assert.DoesNotContain("corrupted-test-secret", error.ToString());
        Assert.DoesNotContain("corrupted-test-csrf", error.ToString());
        Assert.DoesNotContain("original-1", error.ToString());
        Assert.Single(RequestUids(fixture, STREAM_PATH));
    }

    /// <summary>已解密但缺少必要 Cookie 的有效状态记录不得匿名发请求，应跳过并使用正常替补。</summary>
    [MongoFact]
    public async Task IncompleteDecryptedCredentialsCannotStartAnonymousRequests()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        await fixture.AddAccountAsync(2);
        foreach (var cookie in new[] { "", "SESSDATA=incomplete-session", "bili_jct=incomplete-csrf" })
        {
            var credentials = new BilibiliLoginCredentials { Cookie = cookie };
            await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").UpdateOneAsync(
                account => account.Id == preferred.Id,
                Builders<BilibiliAccount>.Update.Set(account => account.ProtectedCredentials, fixture.Identity.Protector.Protect(JsonSerializer.Serialize(credentials))));
            fixture.Requests.Clear();

            await fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);

            Assert.Equal([2L], RequestUids(fixture, STREAM_PATH));
            Assert.Contains("SESSDATA=original-2", Assert.Single(fixture.Requests, request => request.Path == STREAM_PATH).Cookie);
        }
    }

    /// <summary>上游合法 JSON 中各级空集合或空元素应归为流缺失，并继续尝试其他账号。</summary>
    [MongoFact]
    public async Task NullStreamStructuresBecomeMissingStreamAndAllowFallback()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        await fixture.AddAccountAsync(2);
        var streamShapes = new[]
        {
            "null",
            "[null]",
            """[{"protocol_name":"http_stream","format":null}]""",
            """[{"protocol_name":"http_stream","format":[null]}]""",
            """[{"protocol_name":"http_stream","format":[{"format_name":"flv","codec":null}]}]""",
            """[{"protocol_name":"http_stream","format":[{"format_name":"flv","codec":[null]}]}]""",
            """[{"protocol_name":"http_stream","format":[{"format_name":"flv","codec":[{"codec_name":"avc","url_info":null}]}]}]""",
            """[{"protocol_name":"http_stream","format":[{"format_name":"flv","codec":[{"codec_name":"avc","url_info":[null]}]}]}]"""
        };
        foreach (var streamShape in streamShapes)
        {
            using var document = JsonDocument.Parse(streamShape);
            var response = JsonSerializer.Serialize(new
            {
                code = 0,
                data = new { room_id = 100, live_status = 1, playurl_info = new { playurl = new { stream = document.RootElement } } }
            });
            var allMalformed = false;
            fixture.Requests.Clear();
            fixture.BeforeRequest = request => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.AbsolutePath == STREAM_PATH && (RequestUid(request) == 1 || allMalformed)
                    ? BilibiliAccountTestFixture.Json(response)
                    : null);

            await fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);

            Assert.Equal([1L, 2L], RequestUids(fixture, STREAM_PATH));
            allMalformed = true;
            var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id));
            Assert.Equal("BILIBILI_FLV_STREAM_MISSING", error.Code);
            Assert.Equal([1L, 2L, 1L, 2L], RequestUids(fixture, STREAM_PATH));
            Assert.All(await fixture.Service.ListAccountsAsync(), account => Assert.Equal("Valid", account.Status));
        }
    }

    private static long RequestUid(HttpRequestMessage request) =>
        long.Parse(ApiCookieHelper.GetValue(string.Join(";", request.Headers.GetValues("Cookie")), "DedeUserID"));

    private static long[] RequestUids(BilibiliAccountTestFixture fixture, string path) =>
        fixture.Requests.Where(request => request.Path == path).Select(request => long.Parse(ApiCookieHelper.GetValue(request.Cookie, "DedeUserID"))).ToArray();
}
