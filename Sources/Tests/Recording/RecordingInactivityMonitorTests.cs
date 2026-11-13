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
