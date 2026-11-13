using System.Collections.Concurrent;
using Yohuke.DanmuCore.Services;

namespace Yohuke.LiveRecorder.Tests;

internal sealed class AudienceAvatarTestSource : IUserAvatarSource
{
    private int active;
    private int maxActive;
    internal ConcurrentDictionary<long, int> Calls { get; } = new();
    internal Func<long, CancellationToken, Task<string>> Handle { get; set; } = (uid, _) => Task.FromResult($"https://i0.hdslb.com/users/{uid}.png");
    internal int Active => Volatile.Read(ref active);
    internal int MaxActive => Volatile.Read(ref maxActive);

    /// <inheritdoc />
    public async Task<string> GetAvatarUrlAsync(long userUid, CancellationToken cancellationToken = default)
    {
        Calls.AddOrUpdate(userUid, 1, (_, count) => count + 1);
        var current = Interlocked.Increment(ref active);
        var observed = Volatile.Read(ref maxActive);
        while (current > observed)
        {
            var previous = Interlocked.CompareExchange(ref maxActive, current, observed);
            if (previous == observed)
            {
                break;
            }
            observed = previous;
        }
        try
        {
            return await Handle(userUid, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }
}
