using System.Collections.Concurrent;
using Yohuke.DanmuCore.Services;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class EventArtworkHttpAvatarSource : IUserAvatarSource
{
    internal const string AVATAR_URL = "https://i0.hdslb.com/http-audience.png";
    internal ConcurrentDictionary<long, int> Calls { get; } = new();

    /// <inheritdoc />
    public Task<string> GetAvatarUrlAsync(long userUid, CancellationToken cancellationToken = default)
    {
        Calls.AddOrUpdate(userUid, 1, (_, count) => count + 1);
        return Task.FromResult(userUid == 42 ? AVATAR_URL : "");
    }
}
