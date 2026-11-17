using System.Text.Json;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Recording.Contracts;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证跨重连、暂停和重启的逻辑场次结束判别。</summary>
public sealed class RecordingSessionLifecycleTests
{
    private static DateTimeOffset StartedAtUtc => new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    /// <summary>离线查询和重复准备消息只记录第一次离线时刻。</summary>
    [Fact]
    public void OfflineObservationsPreserveTheOriginalGraceDeadline()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 2, 5);

        Assert.Equal(RecordingSessionEndReason.None, lifecycle.ObserveRoom(false, 100, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(RecordingSessionEndReason.None, lifecycle.ObserveSignal("PREPARING", clock.GetUtcNow().UtcDateTime));
        Assert.Equal(StartedAtUtc.UtcDateTime, lifecycle.State.OfflineSinceUtc);
        Assert.Null(lifecycle.State.EndedAtUtc);

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(RecordingSessionEndReason.OfflineGraceExpired, lifecycle.Evaluate());
        Assert.Equal(StartedAtUtc.AddMinutes(2).UtcDateTime, lifecycle.State.EndedAtUtc);
    }

    /// <summary>等待期内即使开播时间改变，在线查询也继续原逻辑场次。</summary>
    [Fact]
    public void ChangedLiveTimeWithinTheDeadlinesDoesNotEndTheSession()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 2, 5);
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        lifecycle.ObserveRoom(false, 100, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMinutes(1));

        var reason = lifecycle.ObserveRoom(true, 200, clock.GetUtcNow().UtcDateTime);

        Assert.Equal(RecordingSessionEndReason.None, reason);
        Assert.Null(lifecycle.State.OfflineSinceUtc);
        Assert.True(lifecycle.State.LastObservedIsLive);
        Assert.Equal(200, lifecycle.State.LiveTime);
        Assert.Null(lifecycle.State.EndedAtUtc);
    }

    /// <summary>直播消息可以清除未到期的离线等待，但不能刷新媒体无活动上限。</summary>
    [Fact]
    public void LiveSignalsDoNotRefreshTheMediaDeadline()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 2, 1);
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        lifecycle.ObserveSignal("PREPARING", clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal(RecordingSessionEndReason.None, lifecycle.ObserveSignal("LIVE", clock.GetUtcNow().UtcDateTime));
        Assert.Null(lifecycle.State.OfflineSinceUtc);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate());
    }

    /// <summary>宽限到期后到达的在线信号或新开播时间不能复活场次。</summary>
    [Fact]
    public void OnlineObservationCannotEraseAnExpiredOfflineDeadline()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 1, 5);
        lifecycle.ObserveRoom(false, 100, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(RecordingSessionEndReason.OfflineGraceExpired, lifecycle.ObserveRoom(true, 200, clock.GetUtcNow().UtcDateTime));
        Assert.Equal(RecordingSessionEndReason.OfflineGraceExpired, lifecycle.ObserveSignal("LIVE", clock.GetUtcNow().UtcDateTime));
        Assert.Equal(StartedAtUtc.UtcDateTime, lifecycle.State.OfflineSinceUtc);
        Assert.False(lifecycle.State.LastObservedIsLive);
    }

    /// <summary>媒体到期后到达的新数据不能延长已经结束的逻辑场次。</summary>
    [Fact]
    public void MediaAfterTheTimeoutCannotReviveTheSession()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 1);
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        lifecycle.ObserveRoom(true, 100, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime));
        Assert.Equal(StartedAtUtc.UtcDateTime, lifecycle.State.LastMediaReceivedAtUtc);
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.ObserveRoom(true, 200, clock.GetUtcNow().UtcDateTime));
    }

    /// <summary>延迟检查时按照较早到期的条件确定唯一结束原因。</summary>
    [Fact]
    public void DelayedEvaluationUsesTheFirstExpiredDeadline()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 1, 1);
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(30));
        lifecycle.ObserveSignal("PREPARING", clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate());
        Assert.Equal(StartedAtUtc.AddMinutes(1).UtcDateTime, lifecycle.State.EndedAtUtc);
    }

    /// <summary>乱序、重复和无关消息不清除离线事实。</summary>
    [Fact]
    public void StaleAndUnrelatedEventsCannotResetTheOfflineWindow()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 2, 5);
        clock.Advance(TimeSpan.FromSeconds(10));
        lifecycle.ObserveRoom(false, 100, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(10));
        lifecycle.ObserveSignal("LIVE", StartedAtUtc.UtcDateTime);
        lifecycle.ObserveSignal("LIVE", StartedAtUtc.AddSeconds(10).UtcDateTime);
        lifecycle.ObserveSignal("DANMU_MSG", clock.GetUtcNow().UtcDateTime);

        Assert.Equal(StartedAtUtc.AddSeconds(10).UtcDateTime, lifecycle.State.OfflineSinceUtc);
        Assert.Equal(StartedAtUtc.AddSeconds(10).UtcDateTime, lifecycle.State.LastObservationAtUtc);
        Assert.False(lifecycle.State.LastObservedIsLive);
    }

    /// <summary>没有旧媒体接收时间时仅持久化一次兼容计时锚点。</summary>
    [Fact]
    public void LegacyStatePersistsAnAnchorWithoutInventingMedia()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var original = new RecordingSessionLifecycle(new(), clock, 10, 1);
        var state = RoundTrip(original.State);
        Assert.Null(state.LastMediaReceivedAtUtc);
        Assert.Equal(StartedAtUtc.UtcDateTime, state.TimeoutAnchorUtc);
        clock.Advance(TimeSpan.FromSeconds(40));
        var restored = new RecordingSessionLifecycle(state, clock, 10, 1);
        Assert.Equal(StartedAtUtc.UtcDateTime, restored.State.TimeoutAnchorUtc);
        clock.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(RecordingSessionEndReason.MediaTimeout, restored.Evaluate());
        Assert.Null(restored.State.LastMediaReceivedAtUtc);
    }

    /// <summary>恢复后的剩余时间包含进程停止期间，不重新获得完整等待期。</summary>
    [Fact]
    public void RecoveryPreservesElapsedMediaAndOfflineTime()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var original = new RecordingSessionLifecycle(new(), clock, 1, 5);
        original.ReportMedia(clock.GetUtcNow().UtcDateTime);
        original.ObserveSignal("PREPARING", clock.GetUtcNow().UtcDateTime);
        var saved = RoundTrip(original.State);
        clock.Advance(TimeSpan.FromSeconds(40));
        var restoredClock = new RecordingLifecycleTimeProvider(clock.GetUtcNow());
        var restored = new RecordingSessionLifecycle(saved, restoredClock, 1, 5);
        restoredClock.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(RecordingSessionEndReason.OfflineGraceExpired, restored.Evaluate());
        Assert.Equal(StartedAtUtc.AddMinutes(1).UtcDateTime, restored.State.EndedAtUtc);
    }

    /// <summary>恢复已经过期的数据时直接形成终态。</summary>
    [Fact]
    public void RecoveringExpiredStateDoesNotWaitForAnotherObservation()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc.AddMinutes(2));
        var lifecycle = new RecordingSessionLifecycle(new()
        {
            LastMediaReceivedAtUtc = StartedAtUtc.UtcDateTime
        }, clock, 10, 1);

        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.State.EndReason);
        Assert.Equal(StartedAtUtc.AddMinutes(1).UtcDateTime, lifecycle.State.EndedAtUtc);
    }

    /// <summary>真实媒体恢复会刷新无活动上限，但不会改写兼容锚点。</summary>
    [Fact]
    public void ActualMediaReplacesTheFallbackDeadline()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 1);
        clock.Advance(TimeSpan.FromSeconds(40));
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal(RecordingSessionEndReason.None, lifecycle.Evaluate());
        Assert.Equal(StartedAtUtc.UtcDateTime, lifecycle.State.TimeoutAnchorUtc);
        Assert.Equal(StartedAtUtc.AddSeconds(40).UtcDateTime, lifecycle.State.LastMediaReceivedAtUtc);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate());
    }

    /// <summary>运行中的系统时钟前后跳变不改变单调计时截止期。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void WallClockChangesDoNotChangeRunningDeadlines(int shiftSeconds)
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 1);
        clock.Advance(TimeSpan.FromSeconds(40));
        clock.ShiftUtc(TimeSpan.FromSeconds(shiftSeconds));

        Assert.Equal(RecordingSessionEndReason.None, lifecycle.Evaluate());
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate());
    }

    /// <summary>系统时钟调整后的真实媒体继续使用接收原值，并从该活动推进单调截止期。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void ActualMediaAfterAClockChangeKeepsItsRealTimestamp(int shiftSeconds)
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 1);
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(40));
        clock.ShiftUtc(TimeSpan.FromSeconds(shiftSeconds));
        var receivedAtUtc = clock.GetUtcNow().UtcDateTime;

        lifecycle.ReportMedia(receivedAtUtc);
        clock.Advance(TimeSpan.FromSeconds(59));

        Assert.Equal(receivedAtUtc, lifecycle.State.LastMediaReceivedAtUtc);
        Assert.Equal(RecordingSessionEndReason.None, lifecycle.Evaluate());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate());
    }

    /// <summary>系统时钟回拨后的新鲜离线查询仍然能开始宽限计时。</summary>
    [Fact]
    public void FreshRoomObservationAfterAClockRollbackIsNotTreatedAsStale()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 1, 5);
        lifecycle.ObserveRoom(true, 100, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(40));
        clock.ShiftUtc(TimeSpan.FromHours(-1));
        var observedAtUtc = clock.GetUtcNow().UtcDateTime;

        lifecycle.ObserveRoom(false, 100, observedAtUtc);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(observedAtUtc, lifecycle.State.OfflineSinceUtc);
        Assert.Equal(RecordingSessionEndReason.OfflineGraceExpired, lifecycle.Evaluate());
    }

    /// <summary>输入状态和读取快照不能在生命周期锁外改写正在运行的事实。</summary>
    [Fact]
    public void StateSnapshotsCannotMutateTheLifecycle()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var original = new RecordingSessionLifecycleState();
        var lifecycle = new RecordingSessionLifecycle(original, clock, 10, 1);
        original.EndReason = RecordingSessionEndReason.MediaTimeout;
        var snapshot = lifecycle.State;
        snapshot.EndReason = RecordingSessionEndReason.OfflineGraceExpired;
        snapshot.OfflineSinceUtc = StartedAtUtc.AddHours(-1).UtcDateTime;

        Assert.Equal(RecordingSessionEndReason.None, lifecycle.Evaluate());
        Assert.Null(lifecycle.State.OfflineSinceUtc);
        Assert.Equal(StartedAtUtc.UtcDateTime, lifecycle.State.TimeoutAnchorUtc);
    }

    /// <summary>并发房间和消息回调最终只保留时间较新的离线事实。</summary>
    [Fact]
    public async Task ConcurrentObservationsPreserveTheNewestFact()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 5);
        clock.Advance(TimeSpan.FromMinutes(1));

        await Task.WhenAll(
            Task.Run(() => lifecycle.ObserveSignal("LIVE", StartedAtUtc.AddSeconds(40).UtcDateTime)),
            Task.Run(() => lifecycle.ObserveRoom(false, 100, StartedAtUtc.AddSeconds(50).UtcDateTime)));

        Assert.Equal(StartedAtUtc.AddSeconds(50).UtcDateTime, lifecycle.State.LastObservationAtUtc);
        Assert.Equal(StartedAtUtc.AddSeconds(50).UtcDateTime, lifecycle.State.OfflineSinceUtc);
        Assert.False(lifecycle.State.LastObservedIsLive);
    }

    /// <summary>接收新事实才推进持久版本，重复消息和快照读取不产生新版本。</summary>
    [Fact]
    public void AcceptedFactsAdvanceRevisionWhileDuplicatesAndReadsDoNot()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 5);
        var initialized = lifecycle.State.Revision;
        Assert.True(initialized > 0);
        lifecycle.ObserveRoom(true, 100, clock.GetUtcNow().UtcDateTime);
        Assert.Equal(initialized + 1, lifecycle.State.Revision);
        var observed = lifecycle.State.Revision;

        lifecycle.ObserveRoom(true, 100, clock.GetUtcNow().UtcDateTime);
        lifecycle.ObserveSignal("DANMU_MSG", clock.GetUtcNow().UtcDateTime);
        lifecycle.Evaluate();

        Assert.Equal(observed, lifecycle.State.Revision);
        clock.Advance(TimeSpan.FromSeconds(1));
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        Assert.Equal(observed + 1, lifecycle.State.Revision);
    }

    /// <summary>真实活动发生于时钟回拨后时，较小的UTC仍持有更新的状态版本。</summary>
    [Fact]
    public void MediaAfterClockRollbackHasANewerPersistentRevision()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 5);
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        var previous = lifecycle.State;
        clock.Advance(TimeSpan.FromSeconds(30));
        clock.ShiftUtc(TimeSpan.FromHours(-1));

        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);

        Assert.True(lifecycle.State.LastMediaReceivedAtUtc < previous.LastMediaReceivedAtUtc);
        Assert.True(lifecycle.State.Revision > previous.Revision);
    }

    /// <summary>恢复和序列化保留既有版本，不因轮询或交接重建状态机而增长。</summary>
    [Fact]
    public void RecoveryPreservesThePersistentRevision()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var original = new RecordingSessionLifecycle(new(), clock, 10, 5);
        original.ReportMedia(clock.GetUtcNow().UtcDateTime);
        var saved = RoundTrip(original.State);
        clock.Advance(TimeSpan.FromSeconds(1));

        var restored = new RecordingSessionLifecycle(saved, clock, 10, 5);

        Assert.Equal(saved.Revision, restored.State.Revision);
        Assert.Equal(RecordingSessionEndReason.None, restored.Evaluate());
        Assert.Equal(saved.Revision, restored.State.Revision);
    }

    /// <summary>首次结束只递增一次版本，迟到消息和重复到期检查保持同一终态。</summary>
    [Fact]
    public void TerminalTransitionAdvancesRevisionOnlyOnce()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 1);
        var before = lifecycle.State.Revision;
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate());
        Assert.Equal(before + 1, lifecycle.State.Revision);
        lifecycle.ReportMedia(clock.GetUtcNow().UtcDateTime);
        lifecycle.ObserveSignal("LIVE", clock.GetUtcNow().UtcDateTime);
        lifecycle.Evaluate();
        Assert.Equal(before + 1, lifecycle.State.Revision);
    }

    /// <summary>配置缩短等待上限时采用已经累计的时长，终态不能通过延长设置撤销。</summary>
    [Fact]
    public void ConfigurationChangesPreserveElapsedTimeAndTerminalState()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 10, 5);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate(10, 1));
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, lifecycle.Evaluate(10, 10));
    }

    /// <summary>零宽限只在确有离线事实时结束，在线场次不立即结束。</summary>
    [Fact]
    public void ZeroGraceOnlyEndsAfterAnOfflineObservation()
    {
        var clock = new RecordingLifecycleTimeProvider(StartedAtUtc);
        var lifecycle = new RecordingSessionLifecycle(new(), clock, 0, 5);

        Assert.Equal(RecordingSessionEndReason.None, lifecycle.Evaluate());
        Assert.Equal(RecordingSessionEndReason.None, lifecycle.ObserveRoom(true, 100, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(RecordingSessionEndReason.OfflineGraceExpired, lifecycle.ObserveSignal("PREPARING", clock.GetUtcNow().UtcDateTime));
    }

    /// <summary>继续录制契约序列化后保留稳定身份和生命周期事实。</summary>
    [Fact]
    public void ContinuationRoundTripPreservesIdentityAndLifecycle()
    {
        var original = new RecordingSessionContinuation
        {
            LogicalSessionId = "logical-session",
            StartedAtUtc = StartedAtUtc.UtcDateTime,
            Lifecycle = new() { TimeoutAnchorUtc = StartedAtUtc.UtcDateTime, LiveTime = 100 }
        };

        var restored = JsonSerializer.Deserialize<RecordingSessionContinuation>(JsonSerializer.Serialize(original))!;

        Assert.Equal(original, restored);
    }

    private static RecordingSessionLifecycleState RoundTrip(RecordingSessionLifecycleState state) =>
        JsonSerializer.Deserialize<RecordingSessionLifecycleState>(JsonSerializer.Serialize(state))!;
}
