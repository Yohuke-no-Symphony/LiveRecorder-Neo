using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证共享服务重复释放以及在途设置更新的生命周期边界。</summary>
public sealed class BilibiliServiceLifetimeTests
{
    /// <summary>DI 的两个服务注册引用同一实例时，重复释放不会再次取消已销毁的源。</summary>
    [MongoFact]
    public async Task RepeatedDisposalIsSafeAfterCredentialAndDefaultRotations()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var preferred = await fixture.AddAccountAsync(42);
        using var capture = new CancellationTokenSource();
        var subscription = fixture.Service.SubscribeEventsAsync(100, null, (_, _) => Task.CompletedTask, capture.Token);
        await fixture.Connection.NextAsync();
        await fixture.Service.SetDefaultAccountAsync(preferred.Id);
        await fixture.Connection.NextAsync();
        var login = await fixture.Service.BeginAccountQrAsync(preferred.Id);
        await fixture.Service.PollQrAsync(login.SessionId);
        await fixture.Connection.NextAsync();
        await capture.CancelAsync();
        await subscription;

        fixture.Service.Dispose();
        fixture.Service.Dispose();
    }

    /// <summary>释放与默认设置落盘交错时，在途请求不能轮转已销毁的取消源。</summary>
    [MongoFact]
    public async Task InflightDefaultUpdateDoesNotRotateDisposedSignals()
    {
        await using var fixture = await BilibiliAccountTestFixture.CreateAsync();
        await fixture.AddAccountAsync(1);
        var preferred = await fixture.AddAccountAsync(2);
        var settings = new PausingSettingsStore(fixture.Identity.Settings);
        using var service = new BilibiliService(fixture.Identity.Context, fixture.Identity.Protector, fixture.Identity.Http,
            NullLogger<BilibiliService>.Instance, settings, fixture.Connection);
        var update = service.SetDefaultAccountAsync(preferred.Id);
        try
        {
            await settings.Updated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            service.Dispose();
        }
        finally
        {
            settings.Resume.TrySetResult();
        }

        var accounts = await update;
        Assert.Equal(preferred.Id, Assert.Single(accounts, account => account.IsDefault).Id);
        Assert.Equal(preferred.Id, (await fixture.Identity.Settings.GetAsync()).DefaultBilibiliAccountId);
    }
}
