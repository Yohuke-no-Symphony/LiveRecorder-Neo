using Yohuke.LiveRecorder.Infrastructure.Recording;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证录制无媒体时限、恢复计时及运行中修改阈值。</summary>
public sealed class RecordingInactivityMonitorTests
{
    /// <summary>到期前保持有效，到达精确边界时取消采集。</summary>
    [Fact]
    public void DeadlineExpiresAtConfiguredBoundary()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.False(monitor.IsExpired);
        Assert.False(monitor.Token.IsCancellationRequested);

        clock.Advance(TimeSpan.FromTicks(1));

        Assert.True(monitor.IsExpired);
        Assert.True(monitor.Token.IsCancellationRequested);
    }

    /// <summary>恢复媒体后以最后活动时间重新计算时限。</summary>
    [Fact]
    public void MediaActivityRestartsTheSilenceDeadline()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        clock.Advance(TimeSpan.FromSeconds(50));
        monitor.ReportActivity();
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.False(monitor.IsExpired);

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>恢复时只等待持久接收时间之后剩余的无媒体时长。</summary>
    [Fact]
    public void RestoredDeadlineUsesOnlyTheRemainingSilence()
    {
        var clock = new ManualRecordingTimeProvider();
        var received = clock.GetUtcNow().UtcDateTime.AddSeconds(-40);
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock, received);

        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(monitor.IsExpired);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(monitor.IsExpired);
        Assert.True(monitor.Token.IsCancellationRequested);
    }

    /// <summary>停机期间已超过等待上限时不再给予完整的新等待期。</summary>
    [Fact]
    public void AlreadyExpiredRestoredDeadlineCancelsImmediately()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock,
            clock.GetUtcNow().UtcDateTime.AddMinutes(-2));

        Assert.True(monitor.IsExpired);
        Assert.True(monitor.Token.IsCancellationRequested);
        monitor.ReportActivity(clock.GetUtcNow().UtcDateTime);
        Assert.True(monitor.IsExpired);
    }

    /// <summary>恢复后系统 UTC 时钟跳变不改变已经换算的单调截止时间。</summary>
    [Fact]
    public void RestoredDeadlineContinuesUsingMonotonicTime()
    {
        var inner = new ManualRecordingTimeProvider();
        var clock = new RecordingOffsetTimeProvider(inner);
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock,
            clock.GetUtcNow().UtcDateTime.AddSeconds(-40));
        clock.UtcOffset = TimeSpan.FromHours(-1);

        inner.Advance(TimeSpan.FromSeconds(19));
        monitor.Configure(TimeSpan.FromMinutes(1));
        Assert.False(monitor.IsExpired);
        inner.Advance(TimeSpan.FromSeconds(1));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>UTC 回拨之后真正新收到的媒体仍然刷新单调截止期。</summary>
    [Fact]
    public void FreshMediaAfterUtcRollbackStillRefreshesTheDeadline()
    {
        var inner = new ManualRecordingTimeProvider();
        var clock = new RecordingOffsetTimeProvider(inner);
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        inner.Advance(TimeSpan.FromSeconds(50));
        clock.UtcOffset = TimeSpan.FromHours(-1);

        monitor.ReportActivity(clock.GetUtcNow().UtcDateTime);
        inner.Advance(TimeSpan.FromSeconds(59));
        Assert.False(monitor.IsExpired);
        inner.Advance(TimeSpan.FromSeconds(1));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>旧接收戳既不推迟截止时间，也不把已接受的较新活动倒退。</summary>
    [Fact]
    public void OlderActivityDoesNotMoveTheDeadline()
    {
        var clock = new ManualRecordingTimeProvider();
        var received = clock.GetUtcNow().UtcDateTime.AddSeconds(-20);
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock, received);
        clock.Advance(TimeSpan.FromSeconds(10));

        monitor.ReportActivity(received.AddSeconds(-5));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(monitor.IsExpired);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>延迟到达的有效提交按原接收时间延长期限，不按通知时刻重新计时。</summary>
    [Fact]
    public void DelayedActivityUsesTheOriginalReceiptTime()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        clock.Advance(TimeSpan.FromSeconds(40));

        monitor.ReportActivity(clock.GetUtcNow().UtcDateTime.AddSeconds(-10));
        clock.Advance(TimeSpan.FromSeconds(49));
        Assert.False(monitor.IsExpired);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>缩短时限时保留已等待时长，而不是从保存设置时重新计时。</summary>
    [Fact]
    public void ReducingTimeoutIncludesAlreadyElapsedSilence()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(5), clock);
        clock.Advance(TimeSpan.FromMinutes(2));

        monitor.Configure(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.Zero);

        Assert.True(monitor.IsExpired);
        Assert.True(monitor.Token.IsCancellationRequested);
    }

    /// <summary>延长时限只调整截止时间，不清除已累计的无媒体时长。</summary>
    [Fact]
    public void IncreasingTimeoutPreservesTheLastActivityTime()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        clock.Advance(TimeSpan.FromSeconds(50));
        monitor.Configure(TimeSpan.FromMinutes(2));
        clock.Advance(TimeSpan.FromSeconds(69));
        Assert.False(monitor.IsExpired);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>同值配置刷新不会阻止持续故障到期。</summary>
    [Fact]
    public void ReapplyingConfigurationDoesNotExtendTheDeadline()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        clock.Advance(TimeSpan.FromSeconds(40));
        monitor.Configure(TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromSeconds(20));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>取消回调跨线程更新阈值时不会被触发取消的监视器锁阻塞。</summary>
    [Fact]
    public async Task CancellationCallbackCanConfigureTheMonitorFromAnotherThread()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        var callbackCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = monitor.Token.Register(static state =>
        {
            var (current, completion) = ((RecordingInactivityMonitor, TaskCompletionSource<bool>))state!;
            var configure = Task.Run(() => current.Configure(TimeSpan.FromMinutes(2)));
            completion.TrySetResult(configure.Wait(TimeSpan.FromSeconds(2)));
        }, (monitor, callbackCompleted));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Cancellation callbacks must not run while the monitor lock is held.");
        Assert.True(monitor.IsExpired);
    }

    /// <summary>外部停止会取消采集，但不被误判为断流超时。</summary>
    [Fact]
    public void ParentCancellationDoesNotReportInactivityExpiry()
    {
        var clock = new ManualRecordingTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock, cancellation.Token);

        cancellation.Cancel();

        Assert.True(monitor.Token.IsCancellationRequested);
        Assert.False(monitor.IsExpired);
    }
}
