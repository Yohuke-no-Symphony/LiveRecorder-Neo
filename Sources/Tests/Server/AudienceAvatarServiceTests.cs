using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证观众头像查询共享、失败重试、容量与并发边界。</summary>
public sealed class AudienceAvatarServiceTests
{
    [Fact]
    public async Task ConcurrentCallersShareLookupAndCancellationOnlyStopsOneWaiter()
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new AudienceAvatarTestSource { Handle = (_, token) => ready.Task.WaitAsync(token) };
        await using var service = new AudienceAvatarService(source, TimeProvider.System, NullLogger<AudienceAvatarService>.Instance);
        using var cancellation = new CancellationTokenSource();
        var first = service.GetUrlAsync(42, cancellation.Token);
        var second = service.GetUrlAsync(42);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, source.Calls[42]);
        ready.SetResult("https://i0.hdslb.com/shared.png");
        Assert.Equal("https://i0.hdslb.com/shared.png", await second);
        Assert.Equal(await second, await service.GetUrlAsync(42));
        Assert.Equal(1, source.Calls[42]);
    }

    [Fact]
    public async Task FailureIsCachedThenRetriedAfterExpiry()
    {
        var clock = new ArtworkTestTimeProvider(DateTimeOffset.UtcNow);
        var source = new AudienceAvatarTestSource { Handle = (_, _) => Task.FromResult("") };
        await using var service = new AudienceAvatarService(source, clock, NullLogger<AudienceAvatarService>.Instance);

        Assert.Null(await service.GetUrlAsync(42));
        Assert.Null(await service.GetUrlAsync(42));
        Assert.Equal(1, source.Calls[42]);
        clock.Advance(TimeSpan.FromMinutes(2));
        source.Handle = (_, _) => Task.FromResult("https://i0.hdslb.com/recovered.png");

        Assert.Equal("https://i0.hdslb.com/recovered.png", await service.GetUrlAsync(42));
        Assert.Equal(2, source.Calls[42]);
        clock.Advance(TimeSpan.FromHours(7));
        await service.GetUrlAsync(42);
        Assert.Equal(3, source.Calls[42]);
    }

    [Fact]
    public async Task CapacityEvictsLeastRecentlyUsedCompletedLookup()
    {
        var source = new AudienceAvatarTestSource();
        await using var service = new AudienceAvatarService(source, TimeProvider.System, NullLogger<AudienceAvatarService>.Instance, 2);
        await service.GetUrlAsync(1);
        await service.GetUrlAsync(2);
        await service.GetUrlAsync(1);
        await service.GetUrlAsync(3);
        await service.GetUrlAsync(2);

        Assert.Equal(1, source.Calls[1]);
        Assert.Equal(2, source.Calls[2]);
        Assert.Equal(1, source.Calls[3]);
    }

    [Fact]
    public async Task DistinctUsersRespectQueryConcurrencyLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var source = new AudienceAvatarTestSource
        {
            Handle = async (uid, token) =>
            {
                if (Interlocked.Increment(ref count) == 4)
                {
                    entered.TrySetResult();
                }
                await release.Task.WaitAsync(token);
                return $"https://i0.hdslb.com/users/{uid}.png";
            }
        };
        await using var service = new AudienceAvatarService(source, TimeProvider.System, NullLogger<AudienceAvatarService>.Instance);
        var requests = Enumerable.Range(1, 12).Select(uid => service.GetUrlAsync(uid)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, source.Active);
        release.SetResult();
        await Task.WhenAll(requests);

        Assert.Equal(4, source.MaxActive);
        Assert.Equal(12, source.Calls.Count);
    }

    [Fact]
    public async Task PendingQueueIsBoundedWithoutEvictingSharedRequests()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new AudienceAvatarTestSource { Handle = (_, token) => release.Task.WaitAsync(token) };
        await using var service = new AudienceAvatarService(source, TimeProvider.System, NullLogger<AudienceAvatarService>.Instance);
        var requests = Enumerable.Range(1, 128).Select(uid => service.GetUrlAsync(uid)).ToArray();

        Assert.Equal(64, requests.Count(task => task.IsCompletedSuccessfully && task.Result is null));
        release.SetResult("https://i0.hdslb.com/bounded.png");
        await Task.WhenAll(requests);
        Assert.Equal(64, source.Calls.Count);
    }

    [Fact]
    public async Task DisposalCancelsPendingSourceAndCompletesWaitingRequests()
    {
        var source = new AudienceAvatarTestSource
        {
            Handle = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "";
            }
        };
        var service = new AudienceAvatarService(source, TimeProvider.System, NullLogger<AudienceAvatarService>.Instance);
        var request = service.GetUrlAsync(42);

        await service.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await service.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetUrlAsync(42));
    }
}
