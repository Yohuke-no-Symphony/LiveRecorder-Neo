using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>结合平台离线宽限和有效媒体等待上限判定逻辑场次结束。</summary>
public sealed class RecordingSessionLifecycle
{
    private readonly Lock gate = new();
    private readonly RecordingSessionLifecycleState state;
    private readonly TimeProvider timeProvider;
    private long mediaAnchorTimestamp;
    private TimeSpan mediaElapsedAtAnchor;
    private long offlineAnchorTimestamp;
    private TimeSpan offlineElapsedAtAnchor;
    private long observationAnchorTimestamp;
    private TimeSpan observationElapsedAtAnchor;
    private TimeSpan sessionGrace;
    private TimeSpan mediaTimeout;

    /// <summary>恢复持久时间对应的已等待时长，此后通过单调时钟推进截止期。</summary>
    public RecordingSessionLifecycle(RecordingSessionLifecycleState state, TimeProvider timeProvider,
        int sessionGraceMinutes, int streamDisconnectTimeoutMinutes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(state.Revision);
        this.state = state with { };
        this.timeProvider = timeProvider;
        Configure(sessionGraceMinutes, streamDisconnectTimeoutMinutes);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var timestamp = timeProvider.GetTimestamp();
        if (this.state.LastMediaReceivedAtUtc is null && this.state.TimeoutAnchorUtc is null
            && this.state.EndReason == RecordingSessionEndReason.None)
        {
            this.state.TimeoutAnchorUtc = nowUtc;
            AdvanceRevision();
        }
        mediaAnchorTimestamp = timestamp;
        mediaElapsedAtAnchor = ElapsedUtc(this.state.LastMediaReceivedAtUtc ?? this.state.TimeoutAnchorUtc ?? nowUtc, nowUtc);
        offlineAnchorTimestamp = timestamp;
        if (this.state.OfflineSinceUtc is { } offlineSince)
        {
            offlineElapsedAtAnchor = ElapsedUtc(offlineSince, nowUtc);
        }
        observationAnchorTimestamp = timestamp;
        if (this.state.LastObservationAtUtc is { } lastObservation)
        {
            observationElapsedAtAnchor = ElapsedUtc(lastObservation, nowUtc);
        }
        EvaluateCore();
    }

    public RecordingSessionLifecycleState State
    {
        get
        {
            lock (gate)
            {
                return state with { };
            }
        }
    }

    /// <summary>应用一次新鲜的房间状态查询，开播时间变化本身不结束场次。</summary>
    public RecordingSessionEndReason ObserveRoom(bool isLive, long liveTime, DateTime observedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(liveTime);
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        lock (gate)
        {
            return ObserveCore(isLive, liveTime, observedAtUtc);
        }
    }

    /// <summary>将直播或准备消息作为在线或离线观察；其他消息不修改平台状态。</summary>
    public RecordingSessionEndReason ObserveSignal(string command, DateTime receivedAtUtc)
    {
        RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        lock (gate)
        {
            return command switch
            {
                "LIVE" => ObserveCore(true, null, receivedAtUtc),
                "PREPARING" => ObserveCore(false, null, receivedAtUtc),
                _ => EvaluateCore()
            };
        }
    }

    /// <summary>以真实有效媒体的接收时间刷新等待上限，重复或迟到媒体不复活终态。</summary>
    public RecordingSessionEndReason ReportMedia(DateTime receivedAtUtc)
    {
        RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        lock (gate)
        {
            if (EvaluateCore() != RecordingSessionEndReason.None)
            {
                return state.EndReason;
            }
            var timestamp = timeProvider.GetTimestamp();
            var age = ElapsedUtc(receivedAtUtc, timeProvider.GetUtcNow().UtcDateTime);
            var previousAge = mediaElapsedAtAnchor + timeProvider.GetElapsedTime(mediaAnchorTimestamp, timestamp);
            if (state.LastMediaReceivedAtUtc is not null && age >= previousAge)
            {
                return state.EndReason;
            }
            state.LastMediaReceivedAtUtc = receivedAtUtc;
            mediaAnchorTimestamp = timestamp;
            mediaElapsedAtAnchor = age;
            AdvanceRevision();
            return EvaluateCore();
        }
    }

    /// <summary>根据最近配置检查两个截止期，较早到期的条件决定结束原因。</summary>
    public RecordingSessionEndReason Evaluate()
    {
        lock (gate)
        {
            return EvaluateCore();
        }
    }

    /// <summary>更改等待上限并立即检查，不清除此前累计的等待时间。</summary>
    public RecordingSessionEndReason Evaluate(int graceMinutes, int timeoutMinutes)
    {
        lock (gate)
        {
            Configure(graceMinutes, timeoutMinutes);
            return EvaluateCore();
        }
    }

    private RecordingSessionEndReason ObserveCore(bool isLive, long? liveTime, DateTime observedAtUtc)
    {
        if (EvaluateCore() != RecordingSessionEndReason.None)
        {
            return state.EndReason;
        }
        var timestamp = timeProvider.GetTimestamp();
        var age = ElapsedUtc(observedAtUtc, timeProvider.GetUtcNow().UtcDateTime);
        var previousAge = observationElapsedAtAnchor + timeProvider.GetElapsedTime(observationAnchorTimestamp, timestamp);
        if (state.LastObservationAtUtc is not null && age >= previousAge)
        {
            return state.EndReason;
        }
        state.LastObservationAtUtc = observedAtUtc;
        state.LastObservedIsLive = isLive;
        observationAnchorTimestamp = timestamp;
        observationElapsedAtAnchor = age;
        if (liveTime is > 0)
        {
            state.LiveTime = liveTime.Value;
        }
        if (isLive)
        {
            state.OfflineSinceUtc = null;
        }
        else if (state.OfflineSinceUtc is null)
        {
            state.OfflineSinceUtc = observedAtUtc;
            offlineAnchorTimestamp = timestamp;
            offlineElapsedAtAnchor = age;
        }
        AdvanceRevision();
        return EvaluateCore();
    }

    private RecordingSessionEndReason EvaluateCore()
    {
        if (state.EndReason != RecordingSessionEndReason.None)
        {
            return state.EndReason;
        }
        var timestamp = timeProvider.GetTimestamp();
        var mediaRemaining = mediaTimeout - mediaElapsedAtAnchor - timeProvider.GetElapsedTime(mediaAnchorTimestamp, timestamp);
        var offlineRemaining = state.OfflineSinceUtc is null
            ? TimeSpan.MaxValue
            : sessionGrace - offlineElapsedAtAnchor - timeProvider.GetElapsedTime(offlineAnchorTimestamp, timestamp);
        if (mediaRemaining > TimeSpan.Zero && offlineRemaining > TimeSpan.Zero)
        {
            return RecordingSessionEndReason.None;
        }
        if (offlineRemaining <= mediaRemaining)
        {
            state.EndReason = RecordingSessionEndReason.OfflineGraceExpired;
            state.EndedAtUtc = state.OfflineSinceUtc!.Value + sessionGrace;
        }
        else
        {
            state.EndReason = RecordingSessionEndReason.MediaTimeout;
            state.EndedAtUtc = (state.LastMediaReceivedAtUtc ?? state.TimeoutAnchorUtc)!.Value + mediaTimeout;
        }
        AdvanceRevision();
        return state.EndReason;
    }

    private void AdvanceRevision()
    {
        state.Revision = checked(state.Revision + 1);
    }

    private void Configure(int graceMinutes, int timeoutMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(graceMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMinutes);
        sessionGrace = TimeSpan.FromMinutes(graceMinutes);
        mediaTimeout = TimeSpan.FromMinutes(timeoutMinutes);
    }

    private static TimeSpan ElapsedUtc(DateTime startedAtUtc, DateTime nowUtc) =>
        nowUtc > startedAtUtc ? nowUtc - startedAtUtc : TimeSpan.Zero;

    private static void RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("A lifecycle observation must use UTC.", name);
        }
    }
}
