using System.Collections.Concurrent;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class CapturingSessionRevoker : IUserSessionRevoker
{
    internal ConcurrentQueue<(string UserId, long SessionVersion)> Calls { get; } = new();

    public void Revoke(string userId, long sessionVersion)
    {
        Calls.Enqueue((userId, sessionVersion));
    }
}
