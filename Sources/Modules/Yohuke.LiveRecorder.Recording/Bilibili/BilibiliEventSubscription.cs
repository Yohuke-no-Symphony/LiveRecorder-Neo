using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>同一弹幕连接的顺序归档与有界 HTTP 补录，不在 Socket 回调中等待网络。</summary>
public sealed class BilibiliEventSubscription : IDisposable
{
    private readonly ILiveDanmuSocket socket;
    private readonly IBilibiliRawEventSource rawSource;
    private readonly string roomId;
    private readonly string cookie;
    private readonly IBilibiliDanmuHistoryService history;
    private readonly IBilibiliRedPocketService redPockets;
    private readonly ILogger logger;
    private readonly CancellationTokenSource lifetime;
    private readonly BilibiliCapturePolicy policy;
    private readonly BilibiliDanmuReconciliation reconciliation;
    private readonly BilibiliRedPocketRecovery redRecovery;
    private readonly TimeProvider time;
    private readonly Channel<BilibiliCaptureItem> channel;
    private int recoveryRequested = 1;
    private int recoveryFailures;
    private int reportedIncomplete;
    private DateTimeOffset nextHistoryAt;

    /// <summary>注入同一个 Socket 的原始旁路和 Core 查询服务，所有补录共享取消边界。</summary>
    public BilibiliEventSubscription(ILiveDanmuSocket socket, IBilibiliRawEventSource rawSource, long roomId, string cookie, IBilibiliDanmuHistoryService history, IBilibiliRedPocketService redPockets, ILogger logger, CancellationToken cancellationToken, CancellationToken credentialsChanged, BilibiliCapturePolicy? policy = null, BilibiliDanmuReconciliation? reconciliation = null, TimeProvider? timeProvider = null)
    {
        if (!ReferenceEquals(socket, rawSource))
        {
            throw new ArgumentException("原始旁路必须来自同一个弹幕连接。", nameof(rawSource));
        }
        this.policy = policy ?? new();
        if (this.policy.QueueCapacity < 1 || this.policy.MaxHistoryItems < 1 || this.policy.MaxTrackedRedPockets < 1 || this.policy.MaxRedPocketRequestsPerCycle < 1 || this.policy.MaxRedPocketAttempts is < 1 or > 16 || this.policy.SchedulerInterval <= TimeSpan.Zero || this.policy.MinimumHistoryInterval <= TimeSpan.Zero || this.policy.RedPocketRetryInterval <= TimeSpan.Zero || this.policy.RedPocketRetryInterval > TimeSpan.FromMinutes(5) || this.policy.RequestTimeout <= TimeSpan.Zero || this.policy.TrackedRedPocketLifetime <= TimeSpan.Zero || this.policy.DrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
        this.socket = socket;
        this.rawSource = rawSource;
        this.roomId = roomId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        this.cookie = cookie;
        this.history = history;
        this.redPockets = redPockets;
        this.logger = logger;
        this.reconciliation = reconciliation ?? new();
        redRecovery = new(this.policy);
        time = timeProvider ?? TimeProvider.System;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, credentialsChanged);
        channel = Channel.CreateBounded<BilibiliCaptureItem>(new BoundedChannelOptions(this.policy.QueueCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    }

    /// <summary>执行一次连接生命周期，取消或凭据轮换时等待查询和连接一起停止。</summary>
    public async Task RunAsync(Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        await using var cancelRun = cancellationToken.Register(lifetime.Cancel);
        rawSource.RawBusinessEventReceived += OnRawEvent;
        socket.Disconnected += OnDisconnected;
        socket.Recovered += OnRecovered;
        socket.ErrorReceived += OnError;
        using var drain = new CancellationTokenSource();
        await using var stopDrain = lifetime.Token.Register(BeginDrain, drain);
        var producers = CompleteProducersAsync(PumpSocketAsync(), RecoverAsync());
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(drain.Token))
            {
                if (item.Raw is { } raw)
                {
                    var parsed = ParseRaw(raw);
                    if (parsed.Danmu is { } message)
                    {
                        if (reconciliation.AcceptLive(message))
                        {
                            await onEvent(BilibiliEventMapper.MapDanmu(message, raw.ReceivedAtUtc, raw.Command, raw.RawJson), drain.Token).WaitAsync(drain.Token);
                        }
                    }
                    else if (parsed.RedPocket is { } red)
                    {
                        await PublishRedAsync(red, raw.ReceivedAtUtc, raw.Command, raw.RawJson, onEvent, drain.Token);
                    }
                    else
                    {
                        foreach (var value in BilibiliEventMapper.Map(raw))
                        {
                            await onEvent(value, drain.Token).WaitAsync(drain.Token);
                        }
                    }
                }
                else if (item.History is { } messages)
                {
                    foreach (var message in reconciliation.AcceptHistory(messages))
                    {
                        await onEvent(BilibiliEventMapper.MapDanmu(message, item.ObservedAtUtc), drain.Token).WaitAsync(drain.Token);
                    }
                }
                else if (item.RedPocket is { } red)
                {
                    await PublishRedAsync(red, item.ObservedAtUtc, item.Command, null, onEvent, drain.Token);
                }
                ReportIncompleteCoverage();
            }
        }
        catch (OperationCanceledException exception) when (drain.IsCancellationRequested)
        {
            logger.LogError("直播间 {RoomId} 弹幕尾部未在 {DrainSeconds} 秒内排空，已接收消息可能缺失。", roomId, policy.DrainTimeout.TotalSeconds);
            throw new IOException("弹幕停止时未能排空已接收业务包，归档完整性未知。", exception);
        }
        finally
        {
            await lifetime.CancelAsync();
            await producers;
            rawSource.RawBusinessEventReceived -= OnRawEvent;
            socket.Disconnected -= OnDisconnected;
            socket.Recovered -= OnRecovered;
            socket.ErrorReceived -= OnError;
        }
    }

    /// <summary>在执行生命周期结束后释放取消资源。</summary>
    public void Dispose() => lifetime.Dispose();

    private async Task PublishRedAsync(BilibiliRedPocketMessage message, DateTimeOffset observedAt, string command, string? raw, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        if (redRecovery.Observe(message, observedAt) is { } merged)
        {
            await onEvent(BilibiliEventMapper.MapRedPocket(merged, observedAt, command, raw), cancellationToken).WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteProducersAsync(Task socketTask, Task recoveryTask)
    {
        try
        {
            await Task.WhenAll(socketTask, recoveryTask);
            channel.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            channel.Writer.TryComplete(exception);
        }
    }

    private async Task PumpSocketAsync()
    {
        try
        {
            await socket.StartAsync(lifetime.Token);
            if (!lifetime.IsCancellationRequested)
            {
                channel.Writer.TryComplete(new BilibiliEventSourceException(new IOException("弹幕来源连接意外结束。")));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            channel.Writer.TryComplete(new BilibiliEventSourceException(exception));
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    private async Task RecoverAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (Volatile.Read(ref recoveryRequested) != 0 && time.GetUtcNow() >= nextHistoryAt)
                {
                    Interlocked.Exchange(ref recoveryRequested, 0);
                    nextHistoryAt = time.GetUtcNow() + policy.MinimumHistoryInterval;
                    await RecoverRecentAsync(lifetime.Token);
                }
                foreach (var request in redRecovery.TakeDue(time.GetUtcNow()))
                {
                    try
                    {
                        var value = request.Winners
                            ? await redPockets.GetWinnersAsync(roomId, request.LotId, cookie, policy.RequestTimeout, lifetime.Token)
                            : await redPockets.GetInfoAsync(roomId, request.LotId, cookie, policy.RequestTimeout, lifetime.Token);
                        if (value is not null)
                        {
                            Enqueue(new() { RedPocket = value, ObservedAtUtc = time.GetUtcNow(), Command = request.Winners ? "RECORDER_RED_POCKET_WINNERS" : "RECORDER_RED_POCKET_INFO" });
                        }
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (BilibiliAuthenticationException exception)
                    {
                        throw new BilibiliEventSourceException(exception);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning("直播间 {RoomId} 红包 {LotId} 补录暂不可用：{ErrorType}", roomId, request.LotId, exception.GetType().Name);
                    }
                }
                ReportIncompleteCoverage();
                await Task.Delay(policy.SchedulerInterval, time, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            channel.Writer.TryComplete(exception);
            await lifetime.CancelAsync();
        }
    }

    private async Task RecoverRecentAsync(CancellationToken cancellationToken)
    {
        var failed = false;
        try
        {
            var messages = await history.GetRecentAsync(roomId, cookie, policy.RequestTimeout, cancellationToken);
            Enqueue(new() { History = messages.Take(policy.MaxHistoryItems).ToArray(), ObservedAtUtc = time.GetUtcNow() });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BilibiliAuthenticationException exception)
        {
            throw new BilibiliEventSourceException(exception);
        }
        catch (Exception exception)
        {
            failed = true;
            Enqueue(new() { Raw = Diagnostic("RECORDER_HISTORY_FAILED", "近期普通弹幕补录暂不可用，断连期间消息不能保证完整。") });
            logger.LogWarning("直播间 {RoomId} 历史补录暂不可用：{ErrorType}", roomId, exception.GetType().Name);
        }
        try
        {
            var active = await redPockets.GetActiveAsync(roomId, cookie, policy.RequestTimeout, cancellationToken);
            foreach (var message in active.Take(policy.MaxTrackedRedPockets))
            {
                Enqueue(new() { RedPocket = message, ObservedAtUtc = time.GetUtcNow(), Command = "RECORDER_RED_POCKET_ACTIVE" });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BilibiliAuthenticationException exception)
        {
            throw new BilibiliEventSourceException(exception);
        }
        catch (Exception exception)
        {
            failed = true;
            Enqueue(new() { Raw = Diagnostic("RECORDER_RED_POCKET_FAILED", "红包列表补录暂不可用，已有红包的完整性未知。") });
            logger.LogWarning("直播间 {RoomId} 红包列表补录暂不可用：{ErrorType}", roomId, exception.GetType().Name);
        }
        if (failed && Interlocked.Increment(ref recoveryFailures) < 3)
        {
            Interlocked.Exchange(ref recoveryRequested, 1);
        }
    }

    private void OnRawEvent(object? sender, BilibiliRawBusinessEvent value) => Enqueue(new() { Raw = value });
    private void OnDisconnected(object? sender, EventArgs _) => Enqueue(new() { Raw = Diagnostic("RECORDER_DISCONNECTED", "弹幕连接中断，期间消息完整性未知。") });
    private void OnRecovered(object? sender, EventArgs _)
    {
        Enqueue(new() { Raw = Diagnostic("RECORDER_RECOVERED", "弹幕连接已恢复；仅补齐平台仍可查询的消息，缺口不自动消除。") });
        Interlocked.Exchange(ref recoveryFailures, 0);
        Interlocked.Exchange(ref recoveryRequested, 1);
    }
    private void OnError(object? sender, Exception error) => logger.LogWarning("直播间 {RoomId} 弹幕连接报告 {ErrorType}", roomId, error.GetType().Name);

    private void BeginDrain(object? state) => ((CancellationTokenSource)state!).CancelAfter(policy.DrainTimeout);

    private (BilibiliDanmuMessage? Danmu, BilibiliRedPocketMessage? RedPocket) ParseRaw(BilibiliRawBusinessEvent raw)
    {
        try
        {
            var command = raw.Command.Split(':', 2)[0];
            if (command == "DANMU_MSG")
            {
                return (BilibiliLiveWebSocket.ParseDanmuMessage(roomId, JObject.Parse(raw.RawJson)), null);
            }
            if (command.StartsWith("POPULARITY_RED_POCKET", StringComparison.Ordinal))
            {
                return (null, BilibiliBusinessEventParser.ParseRedPocket(roomId, JObject.Parse(raw.RawJson)));
            }
        }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or FormatException or InvalidDataException or OverflowException or ArgumentException)
        {
            logger.LogWarning("直播间 {RoomId} 的 {Command} 无法解析，将保存原始业务包：{ErrorType}", roomId, raw.Command, exception.GetType().Name);
        }
        return default;
    }

    private void ReportIncompleteCoverage()
    {
        if (redRecovery.CoverageIncomplete && Interlocked.Exchange(ref reportedIncomplete, 1) == 0)
        {
            Enqueue(new() { Raw = Diagnostic("RECORDER_RED_POCKET_INCOMPLETE", "红包补齐容量、重试或时限已达到，缺失信息仍为未知。") });
        }
    }

    private BilibiliRawBusinessEvent Diagnostic(string command, string text) => new(roomId, command, new JObject { ["cmd"] = command, ["message"] = text, ["coverageComplete"] = false }.ToString(Newtonsoft.Json.Formatting.None), time.GetUtcNow());

    private void Enqueue(BilibiliCaptureItem value)
    {
        if (!channel.Writer.TryWrite(value))
        {
            channel.Writer.TryComplete(new IOException("弹幕写入队列已满，已停止采集以避免静默丢失。"));
            lifetime.Cancel();
        }
    }
}
