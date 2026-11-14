using System.Diagnostics;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证持续弹幕订阅的账号替补、恢复、退避与跨连接去重边界。</summary>
public sealed class BilibiliEventAccountRetryTests
{
    /// <summary>唯一账号失效后订阅保持等待，重登后的历史补录不得重复此前普通弹幕。</summary>
    [MongoFact]
    public async Task AllExpiredWaitRetainsOrdinaryDanmuReconciliationUntilRelogin()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, account.Id, (_, _) => Task.CompletedTask, capture.Token);
        try
        {
            var original = await fixture.Connection.NextAsync();
            var message = new BilibiliDanmuMessage
            {
                RoomId = "100",
                Uid = 1000,
                UserName = "观众",
                Content = "普通弹幕",
                MessageId = "",
                Timestamp = 1800000000,
                TimestampMilliseconds = 1800000000100
            };
            Assert.True(original.Reconciliation.AcceptLive(message));
            fixture.ExpiredUids.Add(42);

            await fixture.MaintainAsync();
            await original.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAny(subscription, Task.Delay(TimeSpan.FromMilliseconds(100)));

            Assert.False(subscription.IsCompleted);
            Assert.Single(fixture.Connection.Attempts);
            Assert.Equal("Expired", Assert.Single(await fixture.Service.ListAccountsAsync()).Status);
            var login = await fixture.Service.BeginAccountQrAsync(account.Id);
            await fixture.Service.PollQrAsync(login.SessionId);
            var restored = await fixture.Connection.NextAsync();
            Assert.Contains("renewed-42", restored.Cookie);
            Assert.Same(original.Reconciliation, restored.Reconciliation);
            Assert.Empty(restored.Reconciliation.AcceptHistory([message]));
        }
        finally
        {
            await capture.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>暂时失败的首选经成功校验后恢复，无关账号变更不打断正在工作的替补。</summary>
    [MongoFact]
    public async Task SuccessfulValidationRestoresTemporaryPreferenceWithoutUnrelatedReconnects()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(42);
        var defaultAccount = await fixture.AddAccountAsync(1);
        var unrelated = await fixture.AddAccountAsync(3);
        await fixture.Service.SetDefaultAccountAsync(defaultAccount.Id);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, preferred.Id, (_, _) => Task.CompletedTask, capture.Token);
        try
        {
            var original = await fixture.Connection.NextAsync();
            original.Fail(new BilibiliEventSourceException(new HttpRequestException("source unavailable")));
            var fallback = await fixture.Connection.NextAsync();
            Assert.Contains("original-1", fallback.Cookie);
            fixture.LoginUid = 3;

            var login = await fixture.Service.BeginAccountQrAsync(unrelated.Id);
            await fixture.Service.PollQrAsync(login.SessionId);
            await Task.WhenAny(fallback.Stopped.Task, Task.Delay(TimeSpan.FromMilliseconds(100)));

            Assert.False(fallback.Stopped.Task.IsCompleted);
            Assert.False(fallback.CredentialsChanged.IsCancellationRequested);
            Assert.Equal(2, fixture.Connection.Attempts.Count);
            await fixture.MaintainAsync();
            var restored = await fixture.Connection.NextAsync();
            Assert.Contains("original-42", restored.Cookie);
            Assert.True(fallback.CredentialsChanged.IsCancellationRequested);
            Assert.Same(original.Reconciliation, restored.Reconciliation);
            Assert.Equal(3, fixture.Connection.Attempts.Count);
        }
        finally
        {
            await capture.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>显式首选认证失效后切到全局默认，重登后回切且沿用去重上下文。</summary>
    [MongoFact]
    public async Task ExplicitAuthenticationFailureFallsBackAndReloginRestoresPreference()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var defaultAccount = await fixture.AddAccountAsync(2);
        var preferred = await fixture.AddAccountAsync(42);
        await fixture.Service.SetDefaultAccountAsync(defaultAccount.Id);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, preferred.Id, (_, _) => Task.CompletedTask, capture.Token);
        try
        {
            var original = await fixture.Connection.NextAsync();
            Assert.Contains("original-42", original.Cookie);
            original.Fail(new BilibiliEventSourceException(new BilibiliAuthenticationException("expired")));

            var fallback = await fixture.Connection.NextAsync();

            Assert.Contains("original-2", fallback.Cookie);
            Assert.Same(original.Reconciliation, fallback.Reconciliation);
            Assert.Equal("Expired", (await fixture.Service.ListAccountsAsync()).Single(account => account.Id == preferred.Id).Status);
            var login = await fixture.Service.BeginAccountQrAsync(preferred.Id);
            await fixture.Service.PollQrAsync(login.SessionId);
            var restored = await fixture.Connection.NextAsync();
            Assert.Contains("renewed-42", restored.Cookie);
            Assert.True(fallback.CredentialsChanged.IsCancellationRequested);
            Assert.Same(original.Reconciliation, restored.Reconciliation);
            Assert.Equal(defaultAccount.Id, (await fixture.Identity.Settings.GetAsync()).DefaultBilibiliAccountId);
        }
        finally
        {
            await capture.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>维护发现显式首选失效时也主动换号，重新验证有效后主动恢复首选。</summary>
    [MongoFact]
    public async Task MaintenanceExpiryAndRecoveryReevaluateExplicitSubscription()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var preferred = await fixture.AddAccountAsync(2);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, preferred.Id, (_, _) => Task.CompletedTask, capture.Token);
        try
        {
            var original = await fixture.Connection.NextAsync();
            fixture.ExpiredUids.Add(2);

            await fixture.MaintainAsync();
            var fallback = await fixture.Connection.NextAsync();

            Assert.Contains("original-1", fallback.Cookie);
            Assert.True(original.CredentialsChanged.IsCancellationRequested);
            fixture.ExpiredUids.Clear();
            await fixture.MaintainAsync();
            var restored = await fixture.Connection.NextAsync();
            Assert.Contains("original-2", restored.Cookie);
            Assert.True(fallback.CredentialsChanged.IsCancellationRequested);
            Assert.Same(original.Reconciliation, fallback.Reconciliation);
            Assert.Same(original.Reconciliation, restored.Reconciliation);
        }
        finally
        {
            await capture.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>暂时的来源连接失败按首选、默认和其余有效账号轮换，不误标凭据失效。</summary>
    [MongoFact]
    public async Task SourceFailuresRotateAllValidCandidatesWithoutResettingReconciliation()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var defaultAccount = await fixture.AddAccountAsync(2);
        var preferred = await fixture.AddAccountAsync(3);
        await fixture.Service.SetDefaultAccountAsync(defaultAccount.Id);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, preferred.Id, (_, _) => Task.CompletedTask, capture.Token);
        try
        {
            var first = await fixture.Connection.NextAsync();
            Assert.Contains("original-3", first.Cookie);
            first.Fail(new BilibiliEventSourceException(new HttpRequestException("source unavailable")));
            var second = await fixture.Connection.NextAsync();
            Assert.Contains("original-2", second.Cookie);
            second.Fail(new BilibiliEventSourceException(new HttpRequestException("source unavailable")));
            var third = await fixture.Connection.NextAsync();

            Assert.Contains("original-1", third.Cookie);
            Assert.Same(first.Reconciliation, second.Reconciliation);
            Assert.Same(first.Reconciliation, third.Reconciliation);
            Assert.Equal(3, fixture.Connection.Attempts.Count);
            Assert.All(await fixture.Service.ListAccountsAsync(), account => Assert.Equal("Valid", account.Status));
        }
        finally
        {
            await capture.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>所有账号都暂时失败后先退避再重试，去重状态跨轮保留且等待可以取消。</summary>
    [MongoFact]
    public async Task ExhaustedRoundBacksOffAndPreservesReconciliationUntilCancelled()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        await fixture.AddAccountAsync(2);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, preferred.Id, (_, _) => Task.CompletedTask, capture.Token);
        try
        {
            var first = await fixture.Connection.NextAsync();
            first.Fail(new BilibiliEventSourceException(new HttpRequestException("source unavailable")));
            var second = await fixture.Connection.NextAsync();
            var backoff = Stopwatch.StartNew();
            second.Fail(new BilibiliEventSourceException(new HttpRequestException("source unavailable")));

            var nextRound = await fixture.Connection.NextAsync();

            Assert.True(backoff.Elapsed >= TimeSpan.FromMilliseconds(100));
            Assert.Contains("original-1", nextRound.Cookie);
            Assert.Same(first.Reconciliation, nextRound.Reconciliation);
            nextRound.Fail(new BilibiliEventSourceException(new HttpRequestException("source unavailable")));
            var last = await fixture.Connection.NextAsync();
            Assert.Contains("original-2", last.Cookie);
            Assert.Same(first.Reconciliation, last.Reconciliation);
            last.Fail(new BilibiliEventSourceException(new HttpRequestException("source unavailable")));

            await capture.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(4, fixture.Connection.Attempts.Count);
        }
        finally
        {
            await capture.CancelAsync();
            await subscription.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>非来源异常必须返回调用者，不能被换号当成可恢复连接故障。</summary>
    [MongoFact]
    public async Task ArchiveFailurePropagatesWithoutSwitchingAccounts()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var preferred = await fixture.AddAccountAsync(1);
        await fixture.AddAccountAsync(2);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, preferred.Id, (_, _) => Task.CompletedTask, capture.Token);
        var connection = await fixture.Connection.NextAsync();
        var failure = new IOException("archive write failed");

        connection.Fail(failure);
        var observed = await Assert.ThrowsAsync<IOException>(() => subscription.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Same(failure, observed);
        Assert.Single(fixture.Connection.Attempts);
    }
}
