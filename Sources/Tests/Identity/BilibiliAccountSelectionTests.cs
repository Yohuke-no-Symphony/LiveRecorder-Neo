using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证管理员首选身份的持久化、失效回退、恢复及连接生命周期。</summary>
public sealed class BilibiliAccountSelectionTests
{
    /// <summary>默认指针持久化，显式账号保持独立，清空后按创建顺序自动选择。</summary>
    [MongoFact]
    public async Task SelectionPersistsAndExplicitAccountsRemainIndependent()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var first = await fixture.AddAccountAsync(1);
        var selected = await fixture.AddAccountAsync(2);
        var accounts = await fixture.Service.SetDefaultAccountAsync(selected.Id);
        Assert.Equal(selected.Id, Assert.Single(accounts, account => account.IsDefault).Id);
        Assert.Equal(selected.Id, Assert.Single(accounts, account => account.IsEffectiveDefault).Id);
        using var restarted = new BilibiliService(fixture.Identity.Context, fixture.Identity.Protector, fixture.Identity.Http,
            NullLogger<BilibiliService>.Instance, fixture.Identity.Settings, fixture.Connection);

        await restarted.ResolveStreamAsync(100, 10000);
        await restarted.ResolveStreamAsync(100, 10000, first.Id);

        Assert.Collection(fixture.RoomCookies, cookie => Assert.Contains("original-2", cookie), cookie => Assert.Contains("original-1", cookie));
        accounts = await fixture.Service.SetDefaultAccountAsync(null);
        Assert.DoesNotContain(accounts, account => account.IsDefault);
        Assert.Equal(first.Id, Assert.Single(accounts, account => account.IsEffectiveDefault).Id);
        Assert.Null((await fixture.Identity.Settings.GetAsync()).DefaultBilibiliAccountId);
    }

    /// <summary>首选账号失效后默认与显式目标均替补，恢复后重新优先原账号。</summary>
    [MongoFact]
    public async Task ExpiredDefaultFallsBackAndRecoversWithoutChangingPreference()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var fallback = await fixture.AddAccountAsync(1);
        var preferred = await fixture.AddAccountAsync(2);
        await fixture.Service.SetDefaultAccountAsync(preferred.Id);
        fixture.ExpiredUids.Add(2);

        await fixture.MaintainAsync();
        var accounts = await fixture.Service.ListAccountsAsync();

        Assert.Equal(preferred.Id, Assert.Single(accounts, account => account.IsDefault).Id);
        Assert.Equal(fallback.Id, Assert.Single(accounts, account => account.IsEffectiveDefault).Id);
        await fixture.Service.ResolveStreamAsync(100, 10000);
        Assert.Contains("original-1", fixture.RoomCookies.Last());
        await fixture.Service.ResolveStreamAsync(100, 10000, preferred.Id);
        Assert.Contains("original-1", fixture.RoomCookies.Last());
        fixture.ExpiredUids.Clear();
        await fixture.MaintainAsync();
        await fixture.Service.ResolveStreamAsync(100, 10000);
        Assert.Contains("original-2", fixture.RoomCookies.Last());
        Assert.Equal(preferred.Id, (await fixture.Identity.Settings.GetAsync()).DefaultBilibiliAccountId);
    }

    /// <summary>切换、失效回退和恢复只重建默认弹幕连接，保持显式连接及跨连接去重状态。</summary>
    [MongoFact]
    public async Task DefaultChangesReconnectOnlyInheritedEventSubscriptions()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var first = await fixture.AddAccountAsync(1);
        var preferred = await fixture.AddAccountAsync(2);
        using var capture = new CancellationTokenSource();
        var inherited = fixture.Service.SubscribeEventsAsync(100, null, (_, _) => Task.CompletedTask, capture.Token);
        var original = await fixture.Connection.NextAsync();
        var explicitTask = fixture.Service.SubscribeEventsAsync(101, first.Id, (_, _) => Task.CompletedTask, capture.Token);
        var explicitConnection = await fixture.Connection.NextAsync();
        try
        {
            await fixture.Service.SetDefaultAccountAsync(preferred.Id);
            var selected = await fixture.Connection.NextAsync();
            Assert.Equal(100, selected.RoomId);
            Assert.Contains("original-2", selected.Cookie);
            Assert.True(original.CredentialsChanged.IsCancellationRequested);
            Assert.False(explicitConnection.CredentialsChanged.IsCancellationRequested);
            Assert.Same(original.Reconciliation, selected.Reconciliation);

            fixture.ExpiredUids.Add(2);
            await fixture.MaintainAsync();
            var fallback = await fixture.Connection.NextAsync();
            Assert.Contains("original-1", fallback.Cookie);
            Assert.Same(original.Reconciliation, fallback.Reconciliation);
            fixture.ExpiredUids.Clear();
            await fixture.MaintainAsync();
            var restored = await fixture.Connection.NextAsync();
            Assert.Contains("original-2", restored.Cookie);
            Assert.False(explicitConnection.CredentialsChanged.IsCancellationRequested);
            Assert.False(capture.IsCancellationRequested);
        }
        finally
        {
            await capture.CancelAsync();
            await Task.WhenAll(inherited, explicitTask);
        }
    }

    /// <summary>删除默认账号清空指针并回退，显式启用引用仍阻止删除。</summary>
    [MongoFact]
    public async Task DeletingDefaultRestoresAutomaticSelectionAndPreservesExplicitReferenceGuard()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        var first = await fixture.AddAccountAsync(1);
        var preferred = await fixture.AddAccountAsync(2);
        await fixture.Service.SetDefaultAccountAsync(preferred.Id);
        var target = new Streamer { RoomId = 100, AccountId = preferred.Id, Enabled = true };
        await fixture.Identity.Context.Collection<Streamer>("streamers").InsertOneAsync(target);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.DeleteAccountAsync(preferred.Id));
        Assert.Equal("BILIBILI_ACCOUNT_IN_USE", error.Code);
        Assert.Equal(preferred.Id, (await fixture.Identity.Settings.GetAsync()).DefaultBilibiliAccountId);

        await fixture.Identity.Context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == target.Id, Builders<Streamer>.Update.Set(x => x.AccountId, null));
        await fixture.Service.DeleteAccountAsync(preferred.Id);
        var remaining = Assert.Single(await fixture.Service.ListAccountsAsync());
        Assert.Equal(first.Id, remaining.Id);
        Assert.True(remaining.IsEffectiveDefault);
        Assert.False(remaining.IsDefault);
        Assert.Null((await fixture.Identity.Settings.GetAsync()).DefaultBilibiliAccountId);
    }

    /// <summary>全部失效时不能匿名降级；缺失或失效账号不能被设为新默认。</summary>
    [MongoFact]
    public async Task UnusableAccountsNeverSilentlyBecomeAnonymous()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.Service.ResolveStreamAsync(100, 10000);
        Assert.Equal("", Assert.Single(fixture.RoomCookies));
        var expired = await fixture.AddAccountAsync(1, "Expired");
        Assert.Equal("BILIBILI_ACCOUNT_EXPIRED", (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000))).Code);
        Assert.Equal("BILIBILI_ACCOUNT_EXPIRED", (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.SetDefaultAccountAsync(expired.Id))).Code);
        Assert.Equal("BILIBILI_ACCOUNT_NOT_FOUND", (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.SetDefaultAccountAsync("missing"))).Code);
        var unavailable = await fixture.AddAccountAsync(2, "Unavailable");
        Assert.Equal("BILIBILI_ACCOUNT_UNAVAILABLE", (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.SetDefaultAccountAsync(unavailable.Id))).Code);
        Assert.Equal("BILIBILI_ACCOUNT_UNAVAILABLE", (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000))).Code);
        await fixture.Service.DeleteAccountAsync(expired.Id);
        Assert.Equal("BILIBILI_ACCOUNT_UNAVAILABLE", (await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.ResolveStreamAsync(100, 10000))).Code);
        Assert.Single(fixture.RoomCookies);
        Assert.False(Assert.Single(await fixture.Service.ListAccountsAsync()).IsEffectiveDefault);
    }
}
