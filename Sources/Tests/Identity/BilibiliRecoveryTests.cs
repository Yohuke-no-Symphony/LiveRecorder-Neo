using Newtonsoft.Json.Linq;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>不连接真人账号的单 Socket 补录、时间语义和有限排空回归。</summary>
public sealed class BilibiliRecoveryTests
{
    [Fact]
    public async Task CoreHistoryAndRawParserPreserveOriginalMilliseconds()
    {
        const long milliseconds = 1800000000123;
        using var http = new HttpClient(new RecoveryHttpHandler((_, _) => Task.FromResult(RecoveryHttpHandler.Json("""{"code":0,"data":{"room":[{"id":"one","uid":42,"nickname":"用户","text":"内容","timestamp":1800000000123}]}}"""))));
        var message = Assert.Single(await new BilibiliDanmuHistoryService(http).GetRecentAsync("100", ""));
        Assert.Equal(1800000000, message.Timestamp);
        Assert.Equal(milliseconds, message.TimestampMilliseconds);
        var live = BilibiliLiveWebSocket.ParseDanmuMessage("100", JObject.Parse(Danmu("one", milliseconds)));
        Assert.Equal(milliseconds, live!.TimestampMilliseconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime, BilibiliEventMapper.MapDanmu(message, DateTimeOffset.UtcNow).SourceTimeUtc);
    }

    [Fact]
    public void ReconciliationDeduplicatesStableIdsAndPreservesSameSecondOccurrences()
    {
        var state = new BilibiliDanmuReconciliation();
        Assert.True(state.AcceptLive(Message("one", 100)));
        Assert.False(state.AcceptLive(Message("one", 100)));
        Assert.True(state.AcceptLive(Message("two", 100)));
        Assert.Empty(state.AcceptHistory([Message("one", 100), Message("two", 100)]));
        Assert.True(state.AcceptLive(Message("", 200)));
        Assert.True(state.AcceptLive(Message("", 300)));
        Assert.Empty(state.AcceptHistory([Message("", 200), Message("", 300)]));
        Assert.Single(state.AcceptHistory([Message("", 400)]));
        Assert.True(state.AcceptLive(Message("", 500)));
        Assert.False(state.AcceptLive(Message("", 400)));
        Assert.True(state.AcceptLive(Message("", 400)));
        var mixed = new BilibiliDanmuReconciliation();
        Assert.True(mixed.AcceptLive(Message("identified", 600)));
        Assert.Single(mixed.AcceptHistory([Message("identified", 600), Message("", 600)]));
        Assert.False(mixed.AcceptLive(Message("", 600)));
        Assert.True(mixed.AcceptLive(Message("", 600)));
    }

    [Fact]
    public async Task OneSocketArchivesGapRecoveryAndDebouncesRecentHistoryRequests()
    {
        var historyCalls = 0;
        await using var fixture = new BilibiliCaptureFixture((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("gethistory", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref historyCalls);
                return Task.FromResult(RecoveryHttpHandler.Json("""{"code":0,"data":{"room":[{"id":"one","uid":42,"nickname":"用户","text":"内容","timestamp":1800000000123}]}}"""));
            }
            return RecoveryHttpHandler.EmptyAsync(request, default);
        }, BilibiliCaptureFixture.FastPolicy(historyInterval: TimeSpan.FromSeconds(2)));
        await fixture.StartAsync();
        await fixture.WaitUntilAsync(() => fixture.Events.Any(x => x.Kind == "Danmu"));
        fixture.Socket.Emit("DANMU_MSG", Danmu("one", 1800000000123));
        fixture.Socket.Disconnect();
        fixture.Socket.Recover();
        for (var index = 0; index < 100; index++)
        {
            fixture.Socket.Emit("DANMU_MSG", Danmu("live-" + index, 1800000000200 + index));
            fixture.Socket.Recover();
        }
        await fixture.WaitUntilAsync(() => fixture.Events.Count(x => x.Kind == "Danmu") == 101);
        await fixture.StopAsync();
        Assert.Equal(1, historyCalls);
        Assert.Equal(1, fixture.Socket.StartCount);
        Assert.Equal(1, fixture.Socket.StopCount);
        Assert.Contains(fixture.Events, x => x.Kind == "Gap" && x.Command == "RECORDER_DISCONNECTED");
        Assert.Contains(fixture.Events, x => x.Kind == "Recovery" && x.Command == "RECORDER_RECOVERED");
        Assert.All(fixture.Events.Where(x => x.Kind is "Gap" or "Recovery"), x => Assert.False(x.Payload!["coverageComplete"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task ReconnectActuallyBackfillsAvailableHistoryWithoutDuplicatingLive()
    {
        var calls = 0;
        await using var fixture = new BilibiliCaptureFixture((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("gethistory", StringComparison.Ordinal))
            {
                return RecoveryHttpHandler.EmptyAsync(request, default);
            }
            var data = Interlocked.Increment(ref calls) == 1 ? "[]" : "[{\"id\":\"missing\",\"uid\":42,\"nickname\":\"用户\",\"text\":\"内容\",\"timestamp\":1800000000456}]";
            return Task.FromResult(RecoveryHttpHandler.Json("{\"code\":0,\"data\":{\"room\":" + data + "}}"));
        });
        await fixture.StartAsync();
        await fixture.WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        fixture.Socket.Disconnect();
        fixture.Socket.Recover();
        await fixture.WaitUntilAsync(() => fixture.Events.Any(x => x.EventId.EndsWith(":missing", StringComparison.Ordinal)));
        fixture.Socket.Emit("DANMU_MSG", Danmu("missing", 1800000000456));
        await fixture.StopAsync();
        Assert.Single(fixture.Events, x => x.Kind == "Danmu");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RedPocketUpdatesAreAppendedAtObservationTimeAndPreserveEarlierDetails()
    {
        var winnerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var infoCalls = 0;
        var winnerCalls = 0;
        var old = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        await using var fixture = new BilibiliCaptureFixture(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("RedPocketGetInfo", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref infoCalls);
                return RecoveryHttpHandler.Json("{\"code\":0,\"data\":{\"lot_info\":[{\"lot_id\":99,\"sender_uid\":42,\"sender_name\":\"发包人\",\"start_time\":" + old + ",\"end_time\":" + (old + 60) + ",\"danmu\":\"红包口令\",\"award_list\":[{\"gift_id\":\"1\",\"gift_name\":\"礼物\"}]}]}}");
            }
            if (path.EndsWith("RedPocketGetWinners", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref winnerCalls);
                winnerRequested.TrySetResult();
                await winnerReady.Task.WaitAsync(token);
                return RecoveryHttpHandler.Json("{\"code\":0,\"data\":{\"lot_id\":99,\"current_time\":" + old + ",\"winner_info\":[{\"uid\":7,\"uname\":\"赢家\",\"gift_id\":\"1\"}]}}");
            }
            return await RecoveryHttpHandler.EmptyAsync(request, token);
        });
        await fixture.StartAsync();
        fixture.Socket.Emit("POPULARITY_RED_POCKET_NEW", "{\"cmd\":\"POPULARITY_RED_POCKET_NEW\",\"data\":{\"lot_id\":99,\"sender_uid\":42,\"sender_name\":\"发包人\",\"start_time\":" + old + ",\"end_time\":" + (old + 60) + "}}");
        await winnerRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Socket.Emit("DANMU_MSG", Danmu("while-http-waits", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await fixture.WaitUntilAsync(() => fixture.Events.Any(x => x.Kind == "Danmu"));
        winnerReady.TrySetResult();
        await fixture.WaitUntilAsync(() => fixture.Events.Any(x => x.Kind == "RedPocket" && x.Payload!["isDrawn"]!.GetValue<bool>()));
        await Task.Delay(100);
        await fixture.StopAsync();
        var stages = fixture.Events.Where(x => x.Kind == "RedPocket").ToArray();
        Assert.True(stages.Length >= 3);
        Assert.All(stages, x => { Assert.Null(x.SourceTimeUtc); Assert.Equal("99", x.TransactionId); Assert.True(new DateTimeOffset(x.ReceivedAtUtc).ToUnixTimeSeconds() > old + 60); });
        var final = stages[^1];
        Assert.True(final.Payload!["isDrawn"]!.GetValue<bool>());
        Assert.Equal("红包口令", final.Payload["redPocket"]!["danmu"]!.GetValue<string>());
        Assert.Single(final.Payload["redPocket"]!["winners"]!.AsArray());
        Assert.Equal(1, infoCalls);
        Assert.Equal(1, winnerCalls);
    }

    [Fact]
    public async Task ActiveRedPocketWithoutDrawTimeStillLoadsDetailsBeforeResults()
    {
        var infoCalls = 0;
        var winnerCalls = 0;
        var old = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        await using var fixture = new BilibiliCaptureFixture((request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("RedPocketActiveList", StringComparison.Ordinal))
            {
                return Task.FromResult(RecoveryHttpHandler.Json("{\"code\":0,\"data\":{\"list\":[{\"lot_id\":123,\"start_time\":" + old + "}]}}"));
            }
            if (path.EndsWith("RedPocketGetInfo", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref infoCalls);
                return Task.FromResult(RecoveryHttpHandler.Json("{\"code\":0,\"data\":{\"lot_info\":[{\"lot_id\":123,\"start_time\":" + old + ",\"end_time\":" + (old + 60) + ",\"danmu\":\"详情口令\"}]}}"));
            }
            if (path.EndsWith("RedPocketGetWinners", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref winnerCalls);
                return Task.FromResult(RecoveryHttpHandler.Json("{\"code\":0,\"data\":{\"lot_id\":123,\"winner_info\":[{\"uid\":7,\"uname\":\"赢家\"}]}}"));
            }
            return RecoveryHttpHandler.EmptyAsync(request, token);
        });
        await fixture.StartAsync();
        await fixture.WaitUntilAsync(() => fixture.Events.Any(x => x.Kind == "RedPocket" && x.Payload!["isDrawn"]!.GetValue<bool>()));
        await fixture.StopAsync();
        Assert.Equal(1, infoCalls);
        Assert.Equal(1, winnerCalls);
        var final = fixture.Events.Last(x => x.Kind == "RedPocket");
        Assert.Equal("详情口令", final.Payload!["redPocket"]!["danmu"]!.GetValue<string>());
        Assert.Null(final.SourceTimeUtc);
    }

    [Fact]
    public async Task RedPocketRecoveryHasBoundedRetriesAndArchivesUnknownCoverage()
    {
        var infoCalls = 0;
        var winnerCalls = 0;
        var old = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        await using var fixture = new BilibiliCaptureFixture((request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("RedPocketGetInfo", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref infoCalls);
            }
            if (path.EndsWith("RedPocketGetWinners", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref winnerCalls);
            }
            return RecoveryHttpHandler.EmptyAsync(request, token);
        });
        await fixture.StartAsync();
        fixture.Socket.Emit("POPULARITY_RED_POCKET_NEW", "{\"cmd\":\"POPULARITY_RED_POCKET_NEW\",\"data\":{\"lot_id\":99,\"start_time\":" + old + ",\"end_time\":" + (old + 60) + "}}");
        await fixture.WaitUntilAsync(() => fixture.Events.Any(x => x.Command == "RECORDER_RED_POCKET_INCOMPLETE"));
        await Task.Delay(150);
        await fixture.StopAsync();
        Assert.Equal(2, infoCalls);
        Assert.Equal(2, winnerCalls);
        var gap = Assert.Single(fixture.Events, x => x.Command == "RECORDER_RED_POCKET_INCOMPLETE");
        Assert.Equal("Gap", gap.Kind);
        Assert.False(gap.Payload!["coverageComplete"]!.GetValue<bool>());
        Assert.DoesNotContain(fixture.Events, x => x.Kind == "RedPocket" && x.Payload!["isDrawn"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnavailableRecentHistoryStopsRetryingWithoutBlockingLiveMessages()
    {
        var calls = 0;
        await using var fixture = new BilibiliCaptureFixture((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("gethistory", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
                throw new HttpRequestException("fixture unavailable");
            }
            return RecoveryHttpHandler.EmptyAsync(request, token);
        });
        await fixture.StartAsync();
        await fixture.WaitUntilAsync(() => fixture.Events.Count(x => x.Command == "RECORDER_HISTORY_FAILED") == 3);
        fixture.Socket.Emit("DANMU_MSG", Danmu("still-live", 1800000000123));
        await fixture.WaitUntilAsync(() => fixture.Events.Any(x => x.Kind == "Danmu"));
        await Task.Delay(100);
        await fixture.StopAsync();
        Assert.Equal(3, calls);
        Assert.All(fixture.Events.Where(x => x.Command == "RECORDER_HISTORY_FAILED"), x => Assert.Equal("Gap", x.Kind));
    }

    [Fact]
    public async Task MalformedBusinessPacketsAreArchivedAndDoNotTerminateTheSocket()
    {
        await using var fixture = new BilibiliCaptureFixture();
        await fixture.StartAsync();
        fixture.Socket.Emit("DANMU_MSG", "not-json");
        fixture.Socket.Emit("POPULARITY_RED_POCKET_NEW", "{\"cmd\":\"POPULARITY_RED_POCKET_NEW\",\"data\":{\"lot_id\":\"not-a-number\"}}");
        fixture.Socket.Emit("DANMU_MSG", Danmu("after-malformed", 1800000000123));
        await fixture.WaitUntilAsync(() => fixture.Events.Count == 3);
        await fixture.StopAsync();
        Assert.Equal(2, fixture.Events.Count(x => x.Kind == "Raw"));
        Assert.All(fixture.Events.Where(x => x.Kind == "Raw"), x => Assert.NotNull(x.Payload!["parseError"]));
        Assert.Single(fixture.Events, x => x.Kind == "Danmu");
        Assert.Equal(1, fixture.Socket.StartCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFailureDrainsAcceptedTailAndPreservesCause(bool authenticationFailure)
    {
        await using var fixture = new BilibiliCaptureFixture();
        await fixture.StartAsync(async (_, token) => await Task.Delay(1, token));
        for (var index = 0; index < 40; index++)
        {
            fixture.Socket.Emit("DANMU_MSG", Danmu("failed-tail-" + index, 1800000000000 + index));
        }
        Exception cause = authenticationFailure ? new BilibiliAuthenticationException("expired") : new HttpRequestException("connection failed");

        fixture.Socket.Fail(cause);
        var failure = await Assert.ThrowsAsync<BilibiliEventSourceException>(() => fixture.Run.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Same(cause, failure.InnerException);
        Assert.Equal(authenticationFailure, failure.IsAuthenticationFailure);
        Assert.Equal(40, fixture.Events.Count(item => item.Kind == "Danmu"));
        Assert.Equal(1, fixture.Socket.StopCount);
    }

    [Fact]
    public async Task UnexpectedSocketReturnIsReportedAsSourceFailure()
    {
        await using var fixture = new BilibiliCaptureFixture();
        await fixture.StartAsync();

        fixture.Socket.Finish();
        var failure = await Assert.ThrowsAsync<BilibiliEventSourceException>(() => fixture.Run.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.IsType<IOException>(failure.InnerException);
        Assert.False(failure.IsAuthenticationFailure);
    }

    [Fact]
    public async Task SocketDiagnosticsDoNotInterruptHealthyCapture()
    {
        await using var fixture = new BilibiliCaptureFixture();
        await fixture.StartAsync();

        fixture.Socket.ReportError(new InvalidDataException("single malformed packet"));
        fixture.Socket.Emit("DANMU_MSG", Danmu("after-diagnostic", 1800000000123));
        await fixture.WaitUntilAsync(() => fixture.Events.Any(item => item.Kind == "Danmu"));

        Assert.False(fixture.Run.IsCompleted);
        Assert.Equal(1, fixture.Socket.StartCount);
        await fixture.StopAsync();
    }

    [Fact]
    public async Task LocalFileConsumerFailureIsNotWrappedAsSourceFailure()
    {
        await using var fixture = new BilibiliCaptureFixture();
        var cause = new IOException("local archive failed");
        await fixture.StartAsync((_, _) => Task.FromException(cause));

        fixture.Socket.Emit("DANMU_MSG", Danmu("local-failure", 1800000000123));
        var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Run.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(cause, failure);
    }

    [Theory]
    [InlineData("gethistory")]
    [InlineData("RedPocketActiveList")]
    [InlineData("RedPocketGetInfo")]
    [InlineData("RedPocketGetWinners")]
    public async Task RecoveryAuthenticationFailureStopsSourceForAccountReplacement(string endpoint)
    {
        await using var fixture = new BilibiliCaptureFixture((request, token) =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith(endpoint, StringComparison.Ordinal)
                ? Task.FromResult(RecoveryHttpHandler.Json("{\"code\":-101,\"message\":\"expired\"}"))
                : RecoveryHttpHandler.EmptyAsync(request, token);
        });
        await fixture.StartAsync();
        if (endpoint is "RedPocketGetInfo" or "RedPocketGetWinners")
        {
            var old = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
            fixture.Socket.Emit("POPULARITY_RED_POCKET_NEW", "{\"cmd\":\"POPULARITY_RED_POCKET_NEW\",\"data\":{\"lot_id\":99,\"start_time\":" + old + ",\"end_time\":" + (old + 60) + "}}");
        }

        var failure = await Assert.ThrowsAsync<BilibiliEventSourceException>(() => fixture.Run.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.True(failure.IsAuthenticationFailure);
        Assert.IsType<BilibiliAuthenticationException>(failure.InnerException);
        Assert.Equal(1, fixture.Socket.StopCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrCredentialRotationCancelsHttpAndDrainsAlreadyReceivedTail(bool rotateCredentials)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new BilibiliCaptureFixture(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException();
        });
        await fixture.StartAsync(async (_, token) => { Assert.False(token.IsCancellationRequested); await Task.Delay(5, token); });
        for (var index = 0; index < 40; index++)
        {
            fixture.Socket.Emit("DANMU_MSG", Danmu("tail-" + index, 1800000000000 + index));
        }
        if (rotateCredentials)
        {
            await fixture.CredentialsChanged.CancelAsync();
            await fixture.Run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else
        {
            await fixture.StopAsync();
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(40, fixture.Events.Count);
        Assert.Equal(1, fixture.Socket.StopCount);
    }

    [Fact]
    public async Task QueueOverflowFaultsExplicitlyAfterDrainingAcceptedItems()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new BilibiliCaptureFixture(policy: BilibiliCaptureFixture.FastPolicy(capacity: 2));
        await fixture.StartAsync(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        fixture.Socket.Emit("DANMU_MSG", Danmu("first", 1800000000000));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (var index = 0; index < 3; index++)
        {
            fixture.Socket.Emit("DANMU_MSG", Danmu("overflow-" + index, 1800000000001 + index));
        }
        release.TrySetResult();
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("队列已满", error.Message);
        Assert.Equal(3, fixture.Events.Count(x => x.Kind == "Danmu"));
    }

    [Fact]
    public async Task SlowFileConsumerHasFiniteDrainDeadlineAndCannotReportSilentSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new BilibiliCaptureFixture(policy: BilibiliCaptureFixture.FastPolicy(drain: TimeSpan.FromMilliseconds(50)));
        await fixture.StartAsync(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        fixture.Socket.Emit("DANMU_MSG", Danmu("blocked", 1800000000000));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.StopAsync());
        Assert.Contains("排空", error.Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("bad", false)]
    [InlineData("-1", false)]
    [InlineData("0", true)]
    public void MissingOrInvalidGoldPriceIsUnknownInsteadOfInventedZero(string? price, bool known)
    {
        var data = new JObject { ["uid"] = 42, ["uname"] = "用户", ["giftId"] = 1, ["giftName"] = "礼物", ["num"] = 1, ["coin_type"] = "gold" };
        if (price is not null)
        {
            data["price"] = price;
        }
        var json = new JObject { ["cmd"] = "SEND_GIFT", ["data"] = data }.ToString();
        var result = Assert.Single(BilibiliEventMapper.Map(new("100", "SEND_GIFT", json, DateTimeOffset.UtcNow)));
        Assert.Equal("Gift", result.Kind);
        Assert.Equal(known ? "ProtocolGold" : "Unknown", result.PriceSource);
        Assert.Equal(known ? 0m : null, result.Amount);
    }

    private static BilibiliDanmuMessage Message(string id, int milliseconds) => new() { RoomId = "100", Uid = 42, UserName = "用户", Content = "内容", MessageId = id, Timestamp = 1800000000, TimestampMilliseconds = 1800000000000 + milliseconds };
    private static string Danmu(string id, long milliseconds) => new JObject { ["cmd"] = "DANMU_MSG", ["msg_id"] = id, ["info"] = new JArray(new JArray(0, 0, 0, 0, milliseconds), "内容", new JArray(42, "用户")) }.ToString();
}
