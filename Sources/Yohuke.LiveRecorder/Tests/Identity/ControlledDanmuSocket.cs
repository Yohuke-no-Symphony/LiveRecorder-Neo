using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuCore.Services;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class ControlledDanmuSocket : ILiveDanmuSocket, IBilibiliRawEventSource
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int StartCount { get; private set; }
    internal int StopCount { get; private set; }
    public event EventHandler<BilibiliRawBusinessEvent>? RawBusinessEventReceived;
    public event EventHandler? Disconnected;
    public event EventHandler? Recovered;
    public event EventHandler<Exception>? ErrorReceived { add { } remove { } }
    public event EventHandler<BilibiliDanmuMessage>? DanmuReceived { add { } remove { } }
    public event EventHandler<BilibiliGiftMessage>? GiftReceived { add { } remove { } }
    public event EventHandler<BilibiliSuperChatMessage>? SuperChatReceived { add { } remove { } }
    public event EventHandler<BilibiliGuardPurchaseMessage>? GuardPurchaseReceived { add { } remove { } }
    public event EventHandler<BilibiliRedPocketMessage>? RedPocketReceived { add { } remove { } }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            StopCount++;
        }
    }

    internal void Emit(string command, string json) => RawBusinessEventReceived?.Invoke(this, new("100", command, json, DateTimeOffset.UtcNow));
    internal void Disconnect() => Disconnected?.Invoke(this, EventArgs.Empty);
    internal void Recover() => Recovered?.Invoke(this, EventArgs.Empty);
}
