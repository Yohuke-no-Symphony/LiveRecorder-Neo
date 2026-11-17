using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Tests;
using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>验证节点采集、平台信号和逻辑场次继续信息的完整集成。</summary>
public sealed class RecordingRoomLifecycleTests
{
    /// <summary>关闭弹幕保存时仍处理平台直播信号，且不创建弹幕归档。</summary>
    [Fact]
    public async Task DisabledDanmuRecordingStillObservesLiveSignalsWithoutCreatingAnArchive()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var configuration = fixture.Configuration;
        var assignment = fixture.Assignment with { RecordDanmu = false };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var clock = new ManualRecordingTimeProvider();
        var source = new RecordingTestCaptureSource();
        var recorder = new RecordingTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(assignment, spool, source, _ => recorder,
            () => configuration, clock, _ => { }, cancellation.Token);
        runner.Start();
        try
        {
            await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = Assert.Single(spool.Sessions());
            clock.Advance(TimeSpan.FromMilliseconds(100));
            var offlineAtUtc = clock.GetUtcNow().UtcDateTime;

            await source.EmitAsync("PREPARING", offlineAtUtc);

            Assert.Equal(offlineAtUtc, Assert.Single(spool.Sessions()).State.Descriptor.Lifecycle!.OfflineSinceUtc);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await source.EmitAsync("LIVE", clock.GetUtcNow().UtcDateTime);
            var current = Assert.Single(spool.Sessions());
            Assert.Equal(original.State.Descriptor.SessionId, current.State.Descriptor.SessionId);
            Assert.Equal(original.State.Descriptor.LogicalSessionId, current.State.Descriptor.LogicalSessionId);
            Assert.Null(current.State.Descriptor.Lifecycle!.OfflineSinceUtc);
            Assert.True(current.State.Descriptor.Lifecycle.LastObservedIsLive);
            Assert.Equal(RecordingSessionEndReason.None, current.State.Descriptor.Lifecycle.EndReason);
            Assert.False(current.State.Descriptor.HasDanmu);
            Assert.Null(current.State.Events);
            Assert.False(Directory.Exists(Path.Combine(current.DirectoryPath, "弹幕")));
        }
        finally
        {
            await cancellation.CancelAsync();
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>准备消息后快速恢复且平台开播时间变化时，新连接沿用同一物理和逻辑场次。</summary>
    [Fact]
    public async Task PreparingThenLiveWithChangedLiveTimeReconnectsWithinTheSameSession()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var configuration = fixture.Configuration;
        var assignment = fixture.Assignment with { RecordDanmu = false, RecordingTimeoutMinutes = 1, SessionGraceMinutes = 1 };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var clock = new ManualRecordingTimeProvider();
        var source = new RecordingTestCaptureSource { LiveStartedAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-10) };
        var recorder = new RecordingRetryTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(assignment, spool, source, _ => recorder,
            () => configuration, clock, _ => { }, cancellation.Token);
        runner.Start();
        try
        {
            await recorder.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = Assert.Single(spool.Sessions()).State.Descriptor;
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await source.EmitAsync("PREPARING", clock.GetUtcNow().UtcDateTime);
            await WaitAsync(() => runner.Snapshot().State == "Retrying");
            source.LiveStartedAtUtc = source.LiveStartedAtUtc.AddHours(1);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await source.EmitAsync("LIVE", clock.GetUtcNow().UtcDateTime);

            await AdvanceUntilAsync(clock, () => recorder.SecondStarted.Task.IsCompleted, TimeSpan.FromSeconds(5));

            var current = Assert.Single(spool.Sessions()).State.Descriptor;
            Assert.True(source.Queries >= 2);
            Assert.Equal(original.SessionId, current.SessionId);
            Assert.Equal(original.LogicalSessionId, current.LogicalSessionId);
            Assert.Equal(original.LogicalStartedAtUtc, current.LogicalStartedAtUtc);
            Assert.False(current.IsClosed);
            Assert.Equal(RecordingSessionEndReason.None, current.Lifecycle!.EndReason);
            Assert.Null(current.Lifecycle.OfflineSinceUtc);
            Assert.Equal(new DateTimeOffset(source.LiveStartedAtUtc).ToUnixTimeSeconds(), current.Lifecycle.LiveTime);
        }
        finally
        {
            await cancellation.CancelAsync();
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>已排空节点的继续信息让新 assignment 创建独立物理采集并保留逻辑身份。</summary>
    [Fact]
    public async Task ANewAssignmentContinuesTheLogicalSessionInANewPhysicalCapture()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var configuration = fixture.Configuration;
        var assignment = fixture.Assignment with { RecordDanmu = false };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var clock = new ManualRecordingTimeProvider();
        RecordingSessionContinuation continuation;
        string originalPhysicalId;
        using (var cancellation = new CancellationTokenSource())
        using (var runner = new RecordingRoomRunner(assignment, spool, new RecordingTestCaptureSource(), _ => new RecordingTestRecorder(),
                   () => configuration, clock, _ => { }, cancellation.Token))
        {
            runner.Start();
            try
            {
                await WaitAsync(() => runner.Snapshot().State == "Recording");
                originalPhysicalId = Assert.Single(spool.Sessions()).State.Descriptor.SessionId;
                runner.Update(assignment with { Enabled = false, ConfigurationRevision = 2 });
                await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                continuation = Assert.IsType<RecordingSessionContinuation>(runner.Snapshot().Continuation);
                Assert.True(Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);
                Assert.Equal(RecordingSessionEndReason.None, continuation.Lifecycle.EndReason);
            }
            finally
            {
                await cancellation.CancelAsync();
                await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var nextAssignment = assignment with
        {
            AssignmentId = "next-assignment", Generation = 2, Continuation = continuation
        };
        spool.SaveAssignment(nextAssignment, fixture.DirectoryPath);
        var nextRecorder = new RecordingTestRecorder();
        using var nextCancellation = new CancellationTokenSource();
        using var nextRunner = new RecordingRoomRunner(nextAssignment, spool, new RecordingTestCaptureSource(), _ => nextRecorder,
            () => configuration, clock, _ => { }, nextCancellation.Token);
        nextRunner.Start();
        try
        {
            await nextRecorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitAsync(() => nextRunner.Snapshot().State == "Recording");
            var current = Assert.Single(spool.Sessions(nextAssignment.AssignmentId)).State.Descriptor;

            Assert.NotEqual(originalPhysicalId, current.SessionId);
            Assert.Equal(continuation.LogicalSessionId, current.LogicalSessionId);
            Assert.Equal(continuation.StartedAtUtc, current.LogicalStartedAtUtc);
            Assert.False(current.IsClosed);
            Assert.Equal(RecordingSessionEndReason.None, current.Lifecycle!.EndReason);
        }
        finally
        {
            await nextCancellation.CancelAsync();
            await nextRunner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>用户停录只封口物理归档，不生成逻辑直播结束事实。</summary>
    [Fact]
    public async Task ExplicitStopSealsThePhysicalCaptureWithoutEndingTheLogicalSession()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var configuration = fixture.Configuration;
        var assignment = fixture.Assignment with { RecordDanmu = false };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var clock = new ManualRecordingTimeProvider();
        var source = new RecordingTestCaptureSource();
        var recorder = new RecordingTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(assignment, spool, source, _ => recorder,
            () => configuration, clock, _ => { }, cancellation.Token);
        runner.Start();
        try
        {
            await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            runner.Update(assignment with { Enabled = false, ConfigurationRevision = 2 });
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            var closed = Assert.Single(spool.Sessions()).State.Descriptor;
            Assert.True(closed.IsClosed);
            Assert.NotNull(closed.EndedAtUtc);
            Assert.Equal(RecordingSessionEndReason.None, closed.Lifecycle!.EndReason);
            Assert.Null(closed.Lifecycle.EndedAtUtc);
            Assert.Equal(closed.SessionId, runner.Snapshot().SessionId);
            var pending = Assert.IsType<RecordingSessionContinuation>(runner.Snapshot().Continuation);
            Assert.Equal(closed.LogicalSessionId, pending.LogicalSessionId);
            Assert.Equal(RecordingSessionEndReason.None, pending.Lifecycle.EndReason);
            Assert.True(recorder.Stopped.Task.IsCompleted);
            Assert.True(source.EventsStopped.Task.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>新的在线事实之后到达的旧准备消息不能中断当前有效媒体连接。</summary>
    [Fact]
    public async Task StalePreparingSignalDoesNotInterruptTheCurrentMediaAttempt()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var configuration = fixture.Configuration;
        var assignment = fixture.Assignment with { RecordDanmu = false };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var clock = new ManualRecordingTimeProvider();
        var source = new RecordingTestCaptureSource();
        var recorder = new RecordingTestRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(assignment, spool, source, _ => recorder,
            () => configuration, clock, _ => { }, cancellation.Token);
        runner.Start();
        try
        {
            await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var staleAtUtc = clock.GetUtcNow().UtcDateTime;
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await source.EmitAsync("LIVE", clock.GetUtcNow().UtcDateTime);
            clock.Advance(TimeSpan.FromMilliseconds(100));

            await source.EmitAsync("PREPARING", staleAtUtc);
            await AdvanceUntilAsync(clock, () => source.Queries >= 2 || recorder.Stopped.Task.IsCompleted, TimeSpan.FromSeconds(5));

            Assert.False(recorder.Stopped.Task.IsCompleted);
            var current = Assert.Single(spool.Sessions()).State.Descriptor;
            Assert.True(current.Lifecycle!.LastObservedIsLive);
            Assert.Null(current.Lifecycle.OfflineSinceUtc);
            Assert.False(current.IsClosed);
        }
        finally
        {
            await cancellation.CancelAsync();
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>旧归档无媒体接收戳时，首次兼容锚点跨 Worker 恢复保留且不伪造媒体活动。</summary>
    [Fact]
    public async Task LegacyTimeoutAnchorPersistsAcrossWorkerRecovery()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var configuration = fixture.Configuration;
        var assignment = fixture.Assignment with { RecordDanmu = false, RecordingTimeoutMinutes = 1 };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        fixture.Session(spool);
        var clock = new ManualRecordingTimeProvider();
        DateTime? anchor;
        using (var cancellation = new CancellationTokenSource())
        {
            var recorder = new RecordingSilentRecorder();
            using var runner = new RecordingRoomRunner(assignment, spool, new RecordingTestCaptureSource(), _ => recorder,
                () => configuration, clock, _ => { }, cancellation.Token);
            runner.Start();
            try
            {
                await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var stored = Assert.Single(spool.Sessions()).State.Descriptor.Lifecycle;
                Assert.NotNull(stored);
                anchor = stored.TimeoutAnchorUtc;
                Assert.NotNull(anchor);
                Assert.Null(stored.LastMediaReceivedAtUtc);
            }
            finally
            {
                await cancellation.CancelAsync();
                await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        clock.Advance(TimeSpan.FromSeconds(40));
        var nextRecorder = new RecordingSilentRecorder();
        using var nextCancellation = new CancellationTokenSource();
        using var restoredRunner = new RecordingRoomRunner(assignment, spool, new RecordingTestCaptureSource(), _ => nextRecorder,
            () => configuration, clock, _ => { }, nextCancellation.Token);
        restoredRunner.Start();
        try
        {
            await nextRecorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var restored = Assert.Single(spool.Sessions()).State.Descriptor;
            Assert.False(restored.IsClosed);
            Assert.Equal(anchor, restored.Lifecycle!.TimeoutAnchorUtc);
            Assert.Null(restored.Lifecycle.LastMediaReceivedAtUtc);
            clock.Advance(TimeSpan.FromSeconds(20));

            await WaitAsync(() => Assert.Single(spool.Sessions()).State.Descriptor.IsClosed);

            var closed = Assert.Single(spool.Sessions()).State.Descriptor;
            Assert.Equal(anchor, closed.Lifecycle!.TimeoutAnchorUtc);
            Assert.Null(closed.Lifecycle.LastMediaReceivedAtUtc);
            Assert.Equal(RecordingSessionEndReason.MediaTimeout, closed.Lifecycle.EndReason);
            Assert.Null(restoredRunner.Snapshot().SessionId);
        }
        finally
        {
            await nextCancellation.CancelAsync();
            await restoredRunner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task AdvanceUntilAsync(ManualRecordingTimeProvider clock, Func<bool> condition, TimeSpan maximumAdvance)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var advanced = TimeSpan.Zero;
        while (!condition())
        {
            if (advanced < maximumAdvance)
            {
                var step = maximumAdvance - advanced < TimeSpan.FromSeconds(1)
                    ? maximumAdvance - advanced : TimeSpan.FromSeconds(1);
                clock.Advance(step);
                advanced += step;
            }
            await Task.Delay(10, timeout.Token);
        }
    }
}
