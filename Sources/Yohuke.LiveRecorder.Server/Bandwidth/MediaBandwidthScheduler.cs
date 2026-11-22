using Yohuke.LiveRecorder.Application;

namespace Yohuke.LiveRecorder.Server.Bandwidth;

/// <summary>以单调时间和字节公平队列分配当前实例的媒体出口预算。</summary>
public sealed class MediaBandwidthScheduler : BackgroundService, IMediaBandwidthScheduler
{
    private static readonly TimeSpan CONFIGURATION_INTERVAL = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SCHEDULING_INTERVAL = TimeSpan.FromMilliseconds(10);
    private readonly Lock gate = new();
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ISettingsStore settingsStore;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<MediaBandwidthScheduler> logger;
    private readonly BandwidthBucket total;
    private readonly BandwidthQueue playback = new(false);
    private readonly BandwidthQueue downloads = new(true);
    private readonly Dictionary<string, BandwidthAccount> accounts = new(StringComparer.Ordinal);
    private readonly HashSet<MediaBandwidthTransfer> transfers = [];
    private BandwidthConfiguration? configuration;
    private long refreshedAt;
    private double downloadBalance;
    private bool stopped;

    /// <summary>创建共享调度器；配置与测试时钟通过依赖注入提供。</summary>
    public MediaBandwidthScheduler(ISettingsStore settingsStore, TimeProvider timeProvider, ILogger<MediaBandwidthScheduler> logger)
    {
        this.settingsStore = settingsStore;
        this.timeProvider = timeProvider;
        this.logger = logger;
        total = new(timeProvider, timeProvider.GetTimestamp());
    }

    /// <inheritdoc />
    public async ValueTask<MediaBandwidthTransfer> OpenTransferAsync(string userId, MediaTrafficKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (kind is not (MediaTrafficKind.Playback or MediaTrafficKind.Download))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshConfigurationAsync().WaitAsync(cancellationToken);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var now = timeProvider.GetTimestamp();
            if (!accounts.TryGetValue(userId, out var account))
            {
                var bucket = new BandwidthBucket(timeProvider, now);
                bucket.Configure(configuration!.UserDownloadRate, now);
                account = new(userId, bucket);
                accounts.Add(userId, account);
            }
            account.Transfers++;
            account.LastActivity = now;
            var transfer = new MediaBandwidthTransfer(this, account, kind);
            transfers.Add(transfer);
            Wake();
            return transfer;
        }
    }

    internal async ValueTask<MediaBandwidthPermit> AcquireAsync(MediaBandwidthTransfer transfer, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        BandwidthRequest request;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped || transfer.Closed, transfer);
            if (transfer.Pending is not null || transfer.HasPermit)
            {
                throw new InvalidOperationException("每个媒体响应只能保留一个待处理读取或字节许可。");
            }
            if (total.Rate == 0 && (transfer.Kind == MediaTrafficKind.Playback || transfer.Account.DownloadBucket.Rate == 0))
            {
                return Grant(transfer, Math.Min(maximumBytes, total.Quantum), false, false, 0);
            }
            request = new(transfer, maximumBytes);
            transfer.Pending = request;
            Queue(transfer.Kind).Enqueue(request);
            Wake();
        }
        using var registration = cancellationToken.UnsafeRegister(_ => Cancel(request, cancellationToken), null);
        return await request.Completion.Task.ConfigureAwait(false);
    }

    internal void Close(MediaBandwidthTransfer transfer)
    {
        lock (gate)
        {
            if (transfer.Closed)
            {
                return;
            }
            transfer.Closed = true;
            transfers.Remove(transfer);
            transfer.Account.Transfers--;
            transfer.Account.LastActivity = timeProvider.GetTimestamp();
            if (transfer.Pending is { } request)
            {
                Queue(transfer.Kind).Remove(request);
                transfer.Pending = null;
                request.Completion.TrySetCanceled();
            }
            Wake();
        }
    }

    internal async Task RefreshConfigurationAsync()
    {
        await configurationGate.WaitAsync(lifetime.Token);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(stopped, this);
                if (configuration is not null && timeProvider.GetElapsedTime(refreshedAt) < CONFIGURATION_INTERVAL)
                {
                    return;
                }
            }
            try
            {
                var next = BandwidthConfiguration.From((await settingsStore.GetAsync(lifetime.Token)).Bandwidth);
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(stopped, this);
                    var now = timeProvider.GetTimestamp();
                    if (configuration != next)
                    {
                        total.Configure(next.TotalRate, now);
                        foreach (var account in accounts.Values)
                        {
                            account.DownloadBucket.Configure(next.UserDownloadRate, now);
                        }
                        downloadBalance = 0;
                        configuration = next;
                    }
                    refreshedAt = now;
                    Wake();
                }
            }
            catch (Exception error) when (error is not OperationCanceledException && error is not ObjectDisposedException)
            {
                lock (gate)
                {
                    if (configuration is null)
                    {
                        throw;
                    }
                    refreshedAt = timeProvider.GetTimestamp();
                }
                logger.LogError(error, "Could not refresh media bandwidth settings. The last valid settings will remain in use.");
            }
        }
        finally
        {
            configurationGate.Release();
        }
    }

    internal bool ProcessPending()
    {
        lock (gate)
        {
            if (stopped || configuration is null)
            {
                return false;
            }
            var now = timeProvider.GetTimestamp();
            total.Refill(now);
            foreach (var account in accounts.Values)
            {
                account.DownloadBucket.Refill(now);
            }
            if (total.Available == 0)
            {
                return false;
            }
            var watching = playback.Candidate(total.Quantum);
            var downloading = downloads.Candidate(total.Quantum);
            if (watching is null && downloading is null)
            {
                return false;
            }
            var competing = watching is not null && downloading is not null;
            var downloadSelected = downloading is not null && (watching is null || configuration.DownloadPercent == 100 ||
                (configuration.DownloadPercent > 0 && downloadBalance > 0));
            var user = downloadSelected ? downloading! : watching!;
            var kind = downloadSelected ? MediaTrafficKind.Download : MediaTrafficKind.Playback;
            var requested = user.Requests.First!.Value.MaximumBytes;
            var bytes = Math.Min(requested, Math.Min(total.Available, user.Deficit));
            if (downloadSelected)
            {
                bytes = Math.Min(bytes, user.Account.DownloadBucket.Available);
            }
            var request = Queue(kind).Take(user, bytes);
            request.Transfer.Pending = null;
            total.Take(bytes);
            if (downloadSelected)
            {
                user.Account.DownloadBucket.Take(bytes);
            }
            var balancePerByte = competing ? configuration.DownloadPercent / 100d - (downloadSelected ? 1 : 0) : 0;
            downloadBalance = competing ? downloadBalance + balancePerByte * bytes : 0;
            var permit = Grant(request.Transfer, bytes, total.Rate > 0,
                downloadSelected && user.Account.DownloadBucket.Rate > 0, balancePerByte);
            request.Completion.SetResult(permit);
            return true;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopRegistration = stoppingToken.UnsafeRegister(_ => Shutdown(), null);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await wake.WaitAsync(stoppingToken);
                while (HasAccounts())
                {
                    if (HasTransfers())
                    {
                        await RefreshConfigurationAsync();
                    }
                    if (ProcessPending())
                    {
                        await Task.Yield();
                        continue;
                    }
                    PruneAccounts();
                    await Task.Delay(SCHEDULING_INTERVAL, timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (stopped)
        {
        }
        finally
        {
            Shutdown();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Shutdown();
        base.Dispose();
    }

    private MediaBandwidthPermit Grant(MediaBandwidthTransfer transfer, int bytes, bool globalCharged, bool userCharged, double balancePerByte)
    {
        transfer.HasPermit = true;
        transfer.Account.Permits++;
        transfer.Account.LastActivity = timeProvider.GetTimestamp();
        return new(bytes, unused =>
        {
            lock (gate)
            {
                transfer.HasPermit = false;
                transfer.Account.Permits--;
                if (globalCharged)
                {
                    total.Refund(unused);
                }
                if (userCharged)
                {
                    transfer.Account.DownloadBucket.Refund(unused);
                }
                Queue(transfer.Kind).Refund(transfer.Account, unused, total.Quantum);
                downloadBalance = Math.Clamp(downloadBalance - balancePerByte * unused, -total.Quantum, total.Quantum);
                Wake();
            }
        });
    }

    private void Cancel(BandwidthRequest request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (request.Node is null)
            {
                return;
            }
            Queue(request.Transfer.Kind).Remove(request);
            request.Transfer.Pending = null;
            request.Completion.TrySetCanceled(cancellationToken);
            Wake();
        }
    }

    private bool HasAccounts()
    {
        lock (gate)
        {
            return !stopped && accounts.Count > 0;
        }
    }

    private bool HasTransfers()
    {
        lock (gate)
        {
            return transfers.Count > 0;
        }
    }

    private void PruneAccounts()
    {
        lock (gate)
        {
            var now = timeProvider.GetTimestamp();
            foreach (var account in accounts.Values.Where(account => account.Transfers == 0 && account.Permits == 0 &&
                         timeProvider.GetElapsedTime(account.LastActivity, now) >= CONFIGURATION_INTERVAL).ToArray())
            {
                accounts.Remove(account.Id);
                playback.Forget(account);
                downloads.Forget(account);
            }
        }
    }

    private void Shutdown()
    {
        lock (gate)
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            foreach (var transfer in transfers.ToArray())
            {
                Close(transfer);
            }
        }
        lifetime.Cancel();
    }

    private BandwidthQueue Queue(MediaTrafficKind kind) => kind == MediaTrafficKind.Download ? downloads : playback;

    private void Wake()
    {
        if (wake.CurrentCount == 0)
        {
            wake.Release();
        }
    }
}
