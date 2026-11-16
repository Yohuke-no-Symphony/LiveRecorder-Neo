using Xunit;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>Verifies independent Worker identities, replacement races and monotonic online status.</summary>
public sealed class WorkerConnectionStatusTests
{
    /// <summary>Closing one process cannot affect other Worker kinds on the same host.</summary>
    [Fact]
    public void ConnectionsAreIndependentByWorkerId()
    {
        var status = new WorkerConnectionStatusTracker();
        var transcoding = status.Open("same-host-tc");
        var clipping = status.Open("same-host-clip");
        status.Open("same-host-rec");
        status.Close("same-host-clip", clipping);
        Assert.True(status.IsOnline("same-host-tc"));
        Assert.True(status.IsOnline("same-host-rec"));
        Assert.False(status.IsOnline("same-host-clip"));
        status.Close("same-host-tc", transcoding);
        Assert.False(status.IsOnline("same-host-tc"));
        Assert.True(status.IsOnline("same-host-rec"));
    }

    /// <summary>A replaced connection cannot close or extend the lifetime of its replacement.</summary>
    [Fact]
    public void OldConnectionCannotModifyItsReplacement()
    {
        var time = new WorkerStatusTimeProvider();
        var status = new WorkerConnectionStatusTracker(time);
        var previous = status.Open("worker");
        var replacement = status.Open("worker");
        status.Close("worker", previous);
        Assert.False(status.IsCurrent("worker", previous));
        Assert.True(status.IsCurrent("worker", replacement));
        Assert.True(status.IsOnline("worker"));
        time.Advance(TimeSpan.FromSeconds(46));
        Assert.False(status.Heartbeat("worker", previous));
        Assert.False(status.IsOnline("worker"));
        Assert.True(status.Heartbeat("worker", replacement));
        Assert.True(status.IsOnline("worker"));
    }

    /// <summary>Wall-clock changes do not turn an expired connection into an online one.</summary>
    [Fact]
    public void LivenessUsesMonotonicTimeInsteadOfWallClock()
    {
        var time = new WorkerStatusTimeProvider();
        var status = new WorkerConnectionStatusTracker(time);
        status.Open("worker");
        time.ChangeWallClock(TimeSpan.FromDays(365));
        Assert.True(status.IsOnline("worker"));
        time.Advance(TimeSpan.FromSeconds(45));
        time.ChangeWallClock(TimeSpan.FromDays(-730));
        Assert.False(status.IsOnline("worker"));
    }

    /// <summary>A restarted server has no live connections even if persisted heartbeat history is recent.</summary>
    [Fact]
    public void UnconnectedAndClosedWorkersAreOffline()
    {
        var status = new WorkerConnectionStatusTracker();
        Assert.False(status.IsOnline("worker"));
        Assert.False(status.Heartbeat("worker", Guid.NewGuid()));
        var id = status.Open("worker");
        status.Close("worker", id);
        Assert.False(status.IsOnline("worker"));
        Assert.False(status.Heartbeat("worker", id));
    }
}
