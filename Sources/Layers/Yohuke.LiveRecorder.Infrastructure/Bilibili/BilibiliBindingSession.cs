using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Application.Bilibili;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

internal sealed class BilibiliBindingSession(BilibiliWebLoginClient client, string? accountId, long? uid) : IDisposable
{
    private int invalidated;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string? AccountId { get; } = accountId;
    internal long? Uid { get; } = uid;
    internal bool IsInvalidated => Volatile.Read(ref invalidated) != 0;
    internal BilibiliWebLoginClient Client { get; } = client;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal DateTime ExpiresAtUtc { get; } = DateTime.UtcNow.AddMinutes(10);
    internal string? QrKey { get; set; }
    internal string? QrUrl { get; set; }
    internal BilibiliLoginChallenge? Challenge { get; set; }
    internal string Cid { get; set; } = "86";
    internal string Tel { get; set; } = "";
    internal string CaptchaKey { get; set; } = "";
    internal DateTime? LastSentAtUtc { get; set; }
    internal BilibiliLoginSession? Completed { get; set; }

    internal void Invalidate() => Interlocked.Exchange(ref invalidated, 1);

    public void Dispose() => Client.Dispose();
}
