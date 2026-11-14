using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证扫码与短信重新登录的身份约束、引用保留及并发失效保护。</summary>
public sealed class BilibiliAccountReloginTests
{
    /// <summary>二维码重新登录原位更新失效账号，保留默认选择与录制引用。</summary>
    [MongoFact]
    public async Task QrReloginPreservesIdentityAndRestoresPreferredAccount()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var account = await fixture.AddAccountAsync(42);
        await fixture.Service.SetDefaultAccountAsync(account.Id);
        await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").UpdateOneAsync(x => x.Id == account.Id, Builders<BilibiliAccount>.Update.Set(x => x.Status, "Expired"));
        var target = new Streamer { RoomId = 100, AccountId = account.Id, Enabled = true };
        await fixture.Identity.Context.Collection<Streamer>("streamers").InsertOneAsync(target);

        var session = await fixture.Service.BeginAccountQrAsync(account.Id);
        var completed = await fixture.Service.PollQrAsync(session.SessionId);
        var repeated = await fixture.Service.PollQrAsync(session.SessionId);

        Assert.Same(completed, repeated);
        Assert.Equal("Complete", completed.Status);
        Assert.Equal(account.Id, completed.Account!.Id);
        Assert.True(completed.Account.IsDefault);
        Assert.True(completed.Account.IsEffectiveDefault);
        var saved = await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(x => x.Id == account.Id).SingleAsync();
        Assert.Equal("Valid", saved.Status);
        Assert.Equal(account.CreatedAtUtc, saved.CreatedAtUtc);
        Assert.Contains("renewed-42", fixture.Identity.Protector.Unprotect(saved.ProtectedCredentials));
        Assert.DoesNotContain("renewed-42", saved.ProtectedCredentials);
        Assert.Equal(account.Id, (await fixture.Identity.Context.Collection<Streamer>("streamers").Find(x => x.Id == target.Id).SingleAsync()).AccountId);
        Assert.Equal(2, (await fixture.Service.ListAccountsAsync()).Count);
    }

    /// <summary>短信重新登录使用原会话挑战并更新同一账号。</summary>
    [MongoFact]
    public async Task SmsReloginUsesSameAccountAndExistingChallengeFlow()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42, "Expired");
        var session = await fixture.Service.BeginAccountSmsAsync(account.Id, new("86", "13800138000"));
        Assert.Equal("Challenge", session.Status);
        var sent = await fixture.Service.SendSmsAsync(session.SessionId, new("challenge-token", Captcha: "ABCDE"));
        Assert.Equal("SmsSent", sent.Status);
        var completed = await fixture.Service.CompleteSmsAsync(session.SessionId, "123456");
        Assert.Equal(account.Id, completed.Account!.Id);
        Assert.Equal("Valid", completed.Account.Status);
        Assert.Single(await fixture.Service.ListAccountsAsync());
    }

    /// <summary>登录其他 UID 时不覆盖原账号，也不额外创建绑定。</summary>
    [MongoFact]
    public async Task WrongIdentityCannotReplaceOrCreateAnAccount()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42, "Expired");
        fixture.LoginUid = 100;
        var session = await fixture.Service.BeginAccountQrAsync(account.Id);

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.PollQrAsync(session.SessionId));

        Assert.Equal("BILIBILI_RELOGIN_IDENTITY_MISMATCH", error.Code);
        var saved = Assert.Single(await fixture.Identity.Context.Collection<BilibiliAccount>("bilibili_accounts").Find(FilterDefinition<BilibiliAccount>.Empty).ToListAsync());
        Assert.Equal(account.Id, saved.Id);
        Assert.Equal(account.ProtectedCredentials, saved.ProtectedCredentials);
        Assert.Equal("Expired", saved.Status);
    }

    /// <summary>删除目标使在途重新登录失效，响应完成后不能复活已删除绑定。</summary>
    [MongoFact]
    public async Task DeletedTargetCannotBeResurrectedByInflightQrLogin()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42, "Expired");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/nav")
            {
                entered.TrySetResult();
                await release.Task;
            }
            return null;
        };
        var session = await fixture.Service.BeginAccountQrAsync(account.Id);
        var completion = fixture.Service.PollQrAsync(session.SessionId);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Service.DeleteAccountAsync(account.Id);
        }
        finally
        {
            release.TrySetResult();
        }
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => completion);
        Assert.Equal("BILIBILI_ACCOUNT_NOT_FOUND", error.Code);
        Assert.Empty(await fixture.Service.ListAccountsAsync());
        Assert.Equal("BILIBILI_ACCOUNT_NOT_FOUND", (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.PollQrAsync(session.SessionId))).Code);
    }

    /// <summary>旧凭据的失效检查晚于重登完成时，不得覆盖有效状态或打断新连接。</summary>
    [MongoFact]
    public async Task StaleMaintenanceCannotExpireNewlyAuthenticatedCredentials()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var account = await fixture.AddAccountAsync(42);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async request =>
        {
            var oldCredentials = request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Any(cookie => cookie.Contains("original-42", StringComparison.Ordinal));
            if (request.RequestUri!.AbsolutePath == "/x/web-interface/nav" && oldCredentials)
            {
                entered.TrySetResult();
                await release.Task;
                return BilibiliAccountTestFixture.Json("""{"code":-101,"message":"expired"}""");
            }
            return null;
        };
        var maintenance = fixture.MaintainAsync();
        using var capture = new CancellationTokenSource();
        Task? subscription = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var session = await fixture.Service.BeginAccountQrAsync(account.Id);
            await fixture.Service.PollQrAsync(session.SessionId);
            subscription = fixture.Service.SubscribeEventsAsync(100, account.Id, (_, _) => Task.CompletedTask, capture.Token);
            var connection = await fixture.Connection.NextAsync();
            release.TrySetResult();
            await maintenance;
            Assert.Equal("Valid", Assert.Single(await fixture.Service.ListAccountsAsync()).Status);
            Assert.False(connection.CredentialsChanged.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
            await maintenance;
            await capture.CancelAsync();
            if (subscription is not null)
            {
                await subscription;
            }
        }
    }
}
