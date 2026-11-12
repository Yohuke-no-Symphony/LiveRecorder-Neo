using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class BilibiliCaptureFixture : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly BilibiliEventSubscription subscription;
    private readonly CancellationTokenSource stop = new();
    internal CancellationTokenSource CredentialsChanged { get; } = new();
    internal ControlledDanmuSocket Socket { get; } = new();
    internal ConcurrentQueue<RecordedEventEnvelope> Events { get; } = new();
    internal Task Run { get; private set; } = Task.CompletedTask;

    internal BilibiliCaptureFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handle = null, BilibiliCapturePolicy? policy = null)
    {
        http = new(new RecoveryHttpHandler(handle ?? RecoveryHttpHandler.EmptyAsync));
        subscription = new(Socket, Socket, 100, "SESSDATA=fixture;bili_jct=csrf", new BilibiliDanmuHistoryService(http), new BilibiliRedPocketService(http), NullLogger.Instance, stop.Token, CredentialsChanged.Token, policy ?? FastPolicy());
    }

    internal async Task StartAsync(Func<RecordedEventEnvelope, CancellationToken, Task>? onEvent = null)
    {
        Run = subscription.RunAsync(async (value, token) =>
        {
            if (onEvent is not null)
            {
                await onEvent(value, token);
            }
            Events.Enqueue(value);
        }, stop.Token);
        await Socket.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    internal async Task StopAsync()
    {
        await stop.CancelAsync();
        await Run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    internal async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            if (Run.IsFaulted)
            {
                await Run;
            }
            await Task.Delay(5);
        }
        Assert.Fail("Capture condition was not observed.");
    }

    internal static BilibiliCapturePolicy FastPolicy(int capacity = 8192, TimeSpan? drain = null, TimeSpan? historyInterval = null) => new()
    {
        QueueCapacity = capacity,
        SchedulerInterval = TimeSpan.FromMilliseconds(10),
        MinimumHistoryInterval = historyInterval ?? TimeSpan.FromMilliseconds(30),
        RedPocketRetryInterval = TimeSpan.FromMilliseconds(30),
        MaxRedPocketAttempts = 2,
        DrainTimeout = drain ?? TimeSpan.FromSeconds(1),
        RequestTimeout = TimeSpan.FromSeconds(1)
    };

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try
        {
            await Run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (IOException)
        {
        }
        finally
        {
            subscription.Dispose();
            http.Dispose();
            stop.Dispose();
            CredentialsChanged.Dispose();
        }
    }
}
