using Microsoft.Extensions.Logging;
using Yohuke.DanmuCore.Services;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>按观众 UID 共享限流头像查询，并缓存成功与暂时不可用的结果。</summary>
public sealed class AudienceAvatarService(IUserAvatarSource source, TimeProvider timeProvider, ILogger<AudienceAvatarService> logger, int capacity = 4096) : IAsyncDisposable
{
    private const int MAX_PENDING_LOOKUPS = 64;
    private static TimeSpan SuccessDuration { get; } = TimeSpan.FromHours(6);
    private static TimeSpan FailureDuration { get; } = TimeSpan.FromMinutes(1);
    private readonly int maxEntries = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object syncRoot = new();
    private readonly Dictionary<long, AudienceAvatarCacheEntry> entries = [];
    private readonly LinkedList<long> recency = [];
    private readonly SemaphoreSlim querySlots = new(4, 4);
    private readonly CancellationTokenSource lifetime = new();
    private int pendingCount;
    private bool isDisposed;
    private Task? disposal;

    /// <summary>查询头像地址；调用方取消等待不会取消其他消息共享的查询。</summary>
    public async Task<string?> GetUrlAsync(long userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userId <= 0)
        {
            return null;
        }
        AudienceAvatarCacheEntry entry;
        var start = false;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (entries.TryGetValue(userId, out var cached) &&
                (!cached.Completion.Task.IsCompleted || timeProvider.GetUtcNow() < cached.ExpiresAt))
            {
                entry = cached;
                recency.Remove(entry.RecencyNode);
                recency.AddFirst(entry.RecencyNode);
            }
            else
            {
                if (cached is not null)
                {
                    RemoveEntry(userId, cached);
                }
                if (pendingCount >= MAX_PENDING_LOOKUPS || !MakeRoom())
                {
                    return null;
                }
                entry = new(userId);
                entries.Add(userId, entry);
                recency.AddFirst(entry.RecencyNode);
                pendingCount++;
                start = true;
            }
        }
        if (start)
        {
            _ = LoadAsync(userId, entry);
        }
        return await entry.Completion.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (disposal is null)
            {
                isDisposed = true;
                disposal = StopAsync(entries.Values.Select(entry => entry.Completion.Task).ToArray());
            }
            return new(disposal);
        }
    }

    private async Task LoadAsync(long userId, AudienceAvatarCacheEntry entry)
    {
        string? result = null;
        Exception? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await querySlots.WaitAsync(timeout.Token);
            try
            {
                var url = await source.GetAvatarUrlAsync(userId, timeout.Token);
                result = string.IsNullOrWhiteSpace(url) ? null : url;
            }
            finally
            {
                querySlots.Release();
            }
        }
        catch (OperationCanceledException error) when (lifetime.IsCancellationRequested)
        {
            failure = error;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or Newtonsoft.Json.JsonException)
        {
            logger.LogDebug(error, "观众 {UserId} 的头像暂时不可用", userId);
        }
        catch (Exception error)
        {
            failure = error;
        }
        lock (syncRoot)
        {
            pendingCount--;
            entry.ExpiresAt = timeProvider.GetUtcNow() + (result is null ? FailureDuration : SuccessDuration);
            if (failure is not null)
            {
                RemoveEntry(userId, entry);
            }
        }
        if (failure is OperationCanceledException)
        {
            entry.Completion.TrySetCanceled();
        }
        else if (failure is not null)
        {
            entry.Completion.TrySetException(failure);
        }
        else
        {
            entry.Completion.TrySetResult(result);
        }
    }

    private bool MakeRoom()
    {
        if (entries.Count < maxEntries)
        {
            return true;
        }
        var node = recency.Last;
        while (node is not null && !entries[node.Value].Completion.Task.IsCompleted)
        {
            node = node.Previous;
        }
        if (node is null)
        {
            return false;
        }
        RemoveEntry(node.Value, entries[node.Value]);
        return true;
    }

    private void RemoveEntry(long userId, AudienceAvatarCacheEntry entry)
    {
        entries.Remove(userId);
        recency.Remove(entry.RecencyNode);
    }

    private async Task StopAsync(Task<string?>[] pending)
    {
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            querySlots.Dispose();
            lifetime.Dispose();
        }
    }
}
