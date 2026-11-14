using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Verifies that speculative playback never consumes foreground capacity or bypasses waiters.</summary>
public sealed class DynamicProcessGateTests
{
    /// <summary>A single available playback slot is reserved exclusively for foreground work.</summary>
    [Fact]
    public void SingleSlotDoesNotAdmitPrefetch()
    {
        using var gate = new DynamicProcessGate();

        Assert.Null(gate.TryAcquirePrefetch(1, CancellationToken.None));
    }

    /// <summary>Only one prefetch runs globally while a remaining slot accepts foreground work.</summary>
    [Fact]
    public async Task PrefetchLeavesForegroundCapacityAndIsExclusive()
    {
        using var gate = new DynamicProcessGate();
        using var prefetch = gate.TryAcquirePrefetch(2, CancellationToken.None);
        Assert.NotNull(prefetch);
        Assert.Null(gate.TryAcquirePrefetch(8, CancellationToken.None));

        using var foreground = await gate.AcquireAsync(2, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(foreground);
    }

    /// <summary>A foreground slot already in use cannot be consumed as the reserved spare slot.</summary>
    [Fact]
    public async Task OccupiedForegroundCapacityDefersPrefetch()
    {
        using var gate = new DynamicProcessGate();
        using var foreground = await gate.AcquireAsync(2, CancellationToken.None);

        Assert.Null(gate.TryAcquirePrefetch(2, CancellationToken.None));
    }

    /// <summary>Foreground requests already waiting have priority even when the configured limit grows.</summary>
    [Fact]
    public async Task QueuedForegroundPreventsPrefetchFromTakingNewCapacity()
    {
        using var gate = new DynamicProcessGate();
        using var foreground = await gate.AcquireAsync(1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var queued = gate.AcquireAsync(1, cancellation.Token);
        Assert.False(queued.IsCompleted);

        Assert.Null(gate.TryAcquirePrefetch(4, CancellationToken.None));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        using var admitted = gate.TryAcquirePrefetch(4, CancellationToken.None);
        Assert.NotNull(admitted);
    }

    /// <summary>Disposing a speculative lease more than once returns its capacity exactly once.</summary>
    [Fact]
    public void PrefetchLeaseCanBeReleasedAndReacquired()
    {
        using var gate = new DynamicProcessGate();
        var prefetch = gate.TryAcquirePrefetch(2, CancellationToken.None);
        Assert.NotNull(prefetch);

        prefetch.Dispose();
        prefetch.Dispose();

        using var replacement = gate.TryAcquirePrefetch(2, CancellationToken.None);
        Assert.NotNull(replacement);
        Assert.Null(gate.TryAcquirePrefetch(2, CancellationToken.None));
    }

    /// <summary>Already canceled requests cannot reserve speculative capacity.</summary>
    [Fact]
    public void CanceledPrefetchDoesNotAcquireCapacity()
    {
        using var gate = new DynamicProcessGate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => gate.TryAcquirePrefetch(2, cancellation.Token));
        using var admitted = gate.TryAcquirePrefetch(2, CancellationToken.None);
        Assert.NotNull(admitted);
    }
}
