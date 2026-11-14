using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Server.Bandwidth;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>使用单调虚拟时间验证媒体预算、公平调度及热更新。</summary>
public sealed class BandwidthSchedulerTests
{
    /// <summary>连接数和读取块大小不改变一个持续请求账号的总份额。</summary>
    [Theory]
    [InlineData(1, 16_384)]
    [InlineData(4, 16_384)]
    [InlineData(16, 37)]
    public async Task FairnessCountsBytesPerAccountInsteadOfConnections(int connections, int firstReadSize)
    {
        var clock = new BandwidthTestTimeProvider();
        var settings = Store(total: 100_000);
        using var scheduler = Scheduler(settings, clock);
        var transfers = new List<MediaBandwidthTransfer> { await scheduler.OpenTransferAsync("one", MediaTrafficKind.Download) };
        for (var index = 0; index < connections; index++)
        {
            transfers.Add(await scheduler.OpenTransferAsync("many", MediaTrafficKind.Download));
        }
        var sizes = transfers.Select((_, index) => index == 0 ? firstReadSize : 16_384).ToArray();

        var sent = await SimulateAsync(scheduler, clock, transfers, sizes, 1_000);

        Assert.InRange(sent.Sum(), 999_000, 1_000_001);
        Assert.InRange(Math.Abs(sent[0] - sent.Skip(1).Sum()), 0, 5_000);
    }

    /// <summary>播放与下载竞争时按保底比例分配实际字节。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(35)]
    [InlineData(100)]
    public async Task DownloadGuaranteeAllocatesTheConfiguredByteShare(int percent)
    {
        var clock = new BandwidthTestTimeProvider();
        using var scheduler = Scheduler(Store(total: 100_000, percent: percent), clock);
        var transfers = new[]
        {
            await scheduler.OpenTransferAsync("watcher", MediaTrafficKind.Playback),
            await scheduler.OpenTransferAsync("downloader", MediaTrafficKind.Download)
        };

        var sent = await SimulateAsync(scheduler, clock, transfers, [16_384, 16_384], 1_000);

        Assert.InRange(sent.Sum(), 999_000, 1_000_001);
        Assert.InRange(Math.Abs(sent[1] - 10_000L * percent), 0, 5_000);
    }

    /// <summary>对方没有请求时，零保底类别也可使用全部空闲带宽。</summary>
    [Theory]
    [InlineData(MediaTrafficKind.Download, 0)]
    [InlineData(MediaTrafficKind.Playback, 100)]
    public async Task IdleClassLendsAllItsBandwidth(MediaTrafficKind kind, int percent)
    {
        var clock = new BandwidthTestTimeProvider();
        using var scheduler = Scheduler(Store(total: 10_000, percent: percent), clock);
        var transfers = new[] { await scheduler.OpenTransferAsync("only", kind) };

        var sent = await SimulateAsync(scheduler, clock, transfers, [16_384], 100);

        Assert.InRange(sent[0], 9_990, 10_001);
    }

    /// <summary>账号硬上限优先于下载保底，未使用的份额返回播放。</summary>
    [Fact]
    public async Task UserCapReturnsUnusedDownloadGuaranteeToPlayback()
    {
        var clock = new BandwidthTestTimeProvider();
        using var scheduler = Scheduler(Store(total: 100_000, user: 10_000, percent: 90), clock);
        var transfers = new[]
        {
            await scheduler.OpenTransferAsync("watcher", MediaTrafficKind.Playback),
            await scheduler.OpenTransferAsync("downloader", MediaTrafficKind.Download),
            await scheduler.OpenTransferAsync("downloader", MediaTrafficKind.Download)
        };

        var sent = await SimulateAsync(scheduler, clock, transfers, [16_384, 16_384, 16_384], 1_000);

        Assert.InRange(sent.Sum(), 999_000, 1_000_001);
        Assert.InRange(sent[1] + sent[2], 99_000, 100_001);
        Assert.InRange(sent[0], 899_000, 901_000);
    }

    /// <summary>不开启智能限速时，账号硬上限仍独立工作，播放保持不限速。</summary>
    [Fact]
    public async Task FixedLimitWorksWithoutSmartModeAndDoesNotThrottlePlayback()
    {
        var clock = new BandwidthTestTimeProvider();
        using var scheduler = Scheduler(Store(user: 100), clock);
        using var playback = await scheduler.OpenTransferAsync("same", MediaTrafficKind.Playback);
        using var ready = await playback.AcquireAsync(32_768);
        Assert.Equal(16_384, ready.Bytes);
        ready.Commit(ready.Bytes);
        var downloads = new[]
        {
            await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download),
            await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download)
        };

        var sent = await SimulateAsync(scheduler, clock, downloads, [16_384, 16_384], 100);

        Assert.InRange(sent.Sum(), 99, 101);
    }

    /// <summary>缓存额度受突发上限约束，只有一字节每秒也不会产生零容量死锁。</summary>
    [Fact]
    public async Task OneByteRateAndBurstBudgetRemainBounded()
    {
        var clock = new BandwidthTestTimeProvider();
        using var scheduler = Scheduler(Store(total: 1, user: 1), clock);
        using var transfer = await scheduler.OpenTransferAsync("slow", MediaTrafficKind.Download);
        var pending = transfer.AcquireAsync(65_536).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(scheduler.ProcessPending());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(scheduler.ProcessPending());
        using (var permit = await pending)
        {
            Assert.Equal(1, permit.Bytes);
            permit.Commit(1);
        }
        pending = transfer.AcquireAsync(65_536).AsTask();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(scheduler.ProcessPending());
        using (var permit = await pending)
        {
            Assert.Equal(1, permit.Bytes);
            permit.Commit(1);
        }
        pending = transfer.AcquireAsync(65_536).AsTask();
        Assert.False(scheduler.ProcessPending());
        transfer.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>高速排队请求在配置降低后按新额度部分批准，关闭限制后也可继续。</summary>
    [Fact]
    public async Task RateChangesReevaluateAlreadyWaitingLargeReads()
    {
        var clock = new BandwidthTestTimeProvider();
        var store = Store(total: 1_000_000, user: 1_000_000);
        using var scheduler = Scheduler(store, clock);
        using var transfer = await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download);
        var pending = transfer.AcquireAsync(65_536).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Settings.Bandwidth = new() { SmartLimitEnabled = true, TotalBytesPerSecond = 1, UserDownloadLimitBytesPerSecond = 1 };
        await scheduler.RefreshConfigurationAsync();

        Assert.True(scheduler.ProcessPending());
        using (var permit = await pending)
        {
            Assert.Equal(1, permit.Bytes);
            permit.Commit(1);
        }
        pending = transfer.AcquireAsync(65_536).AsTask();
        Assert.False(scheduler.ProcessPending());
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Settings.Bandwidth = new();
        await scheduler.RefreshConfigurationAsync();

        Assert.True(scheduler.ProcessPending());
        using var unlimited = await pending;
        Assert.InRange(unlimited.Bytes, 1, 16_384);
    }

    /// <summary>已经打开的不限速传输会采用新的限制。</summary>
    [Fact]
    public async Task UnlimitedTransfersAdoptNewLimits()
    {
        var clock = new BandwidthTestTimeProvider();
        var store = Store();
        using var scheduler = Scheduler(store, clock);
        using var transfer = await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download);
        using (var unlimited = await transfer.AcquireAsync(65_536))
        {
            unlimited.Commit(unlimited.Bytes);
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Settings.Bandwidth = new() { UserDownloadLimitBytesPerSecond = 10 };
        await scheduler.RefreshConfigurationAsync();
        var pending = transfer.AcquireAsync(65_536).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.False(scheduler.ProcessPending());
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(scheduler.ProcessPending());
        using var limited = await pending;
        Assert.Equal(1, limited.Bytes);
    }

    /// <summary>未使用许可只退款一次，同账号重连不会重新获得一桶额度。</summary>
    [Fact]
    public async Task UnusedPermitsRefundOnceAndReconnectKeepsAccountBudget()
    {
        var clock = new BandwidthTestTimeProvider();
        using var scheduler = Scheduler(Store(user: 100), clock);
        using var transfer = await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download);
        var pending = transfer.AcquireAsync(100).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(scheduler.ProcessPending());
        var permit = await pending;
        Assert.Equal(5, permit.Bytes);
        permit.Commit(2);
        permit.Dispose();
        transfer.Dispose();
        using var reconnected = await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download);
        pending = reconnected.AcquireAsync(100).AsTask();
        Assert.True(scheduler.ProcessPending());
        var sentAfterReconnect = 0;
        using (var next = await pending)
        {
            sentAfterReconnect += next.Bytes;
            next.Commit(next.Bytes);
        }
        pending = reconnected.AcquireAsync(100).AsTask();
        Assert.True(scheduler.ProcessPending());
        using (var remaining = await pending)
        {
            sentAfterReconnect += remaining.Bytes;
            remaining.Commit(remaining.Bytes);
        }
        Assert.Equal(8, sentAfterReconnect);
        pending = reconnected.AcquireAsync(100).AsTask();
        Assert.False(scheduler.ProcessPending());
        reconnected.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>取消等待与服务停止均释放队列，不影响其他账号获得许可。</summary>
    [Fact]
    public async Task CancellationAndShutdownReleaseWaiters()
    {
        var clock = new BandwidthTestTimeProvider();
        using var scheduler = Scheduler(Store(total: 100), clock);
        using var first = await scheduler.OpenTransferAsync("first", MediaTrafficKind.Download);
        using var second = await scheduler.OpenTransferAsync("second", MediaTrafficKind.Download);
        using var cancellation = new CancellationTokenSource();
        var cancelled = first.AcquireAsync(100, cancellation.Token).AsTask();
        var waiting = second.AcquireAsync(100).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(scheduler.ProcessPending());
        using (var permit = await waiting)
        {
            permit.Commit(permit.Bytes);
        }
        waiting = second.AcquireAsync(100).AsTask();
        scheduler.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    /// <summary>首次配置读取失败会拒绝传输，后续刷新失败沿用有效限额。</summary>
    [Fact]
    public async Task SettingsFailuresNeverTurnLimitsOff()
    {
        var clock = new BandwidthTestTimeProvider();
        var store = Store(total: 10);
        store.Fail = true;
        using var scheduler = Scheduler(store, clock);
        await Assert.ThrowsAsync<IOException>(async () => await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download));
        store.Fail = false;
        using var transfer = await scheduler.OpenTransferAsync("same", MediaTrafficKind.Download);
        store.Fail = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        await scheduler.RefreshConfigurationAsync();
        var pending = transfer.AcquireAsync(100).AsTask();
        Assert.True(scheduler.ProcessPending());
        using var permit = await pending;
        Assert.Equal(1, permit.Bytes);
    }

    private static BandwidthTestSettingsStore Store(long total = 0, long user = 0, int percent = 10) => new()
    {
        Settings = new ServerSettings
        {
            Bandwidth = new()
            {
                SmartLimitEnabled = total > 0,
                TotalBytesPerSecond = total,
                UserDownloadLimitBytesPerSecond = user,
                DownloadGuaranteedPercent = percent
            }
        }
    };

    private static MediaBandwidthScheduler Scheduler(BandwidthTestSettingsStore store, TimeProvider clock) =>
        new(store, clock, NullLogger<MediaBandwidthScheduler>.Instance);

    private static async Task<long[]> SimulateAsync(MediaBandwidthScheduler scheduler, BandwidthTestTimeProvider clock,
        IReadOnlyList<MediaBandwidthTransfer> transfers, int[] sizes, int ticks)
    {
        var sent = new long[transfers.Count];
        var pending = transfers.Select((transfer, index) => transfer.AcquireAsync(sizes[index]).AsTask()).ToArray();
        for (var tick = 0; tick < ticks; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            while (scheduler.ProcessPending())
            {
                for (var index = 0; index < transfers.Count; index++)
                {
                    if (!transfers[index].HasPermit)
                    {
                        continue;
                    }
                    using var permit = await pending[index];
                    sent[index] += permit.Bytes;
                    permit.Commit(permit.Bytes);
                    pending[index] = transfers[index].AcquireAsync(sizes[index]).AsTask();
                }
            }
        }
        foreach (var transfer in transfers)
        {
            transfer.Dispose();
        }
        foreach (var request in pending)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        return sent;
    }
}
