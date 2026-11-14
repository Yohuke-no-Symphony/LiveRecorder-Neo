using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Infrastructure.Auth;

namespace Yohuke.LiveRecorder.Server.Realtime;

/// <summary>面向已审核用户的状态更新通道。</summary>
public sealed class StatusHub(StatusConnections connections, IIdentityService identity) : Hub
{
    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var version = UserPrincipalFactory.GetSessionVersion(Context.User);
        if (userId is null || version is null)
        {
            Context.Abort();
            return;
        }
        connections.Add(new(Context.ConnectionId, userId, version.Value, Context.Abort));
        try
        {
            if (await identity.ValidateSessionAsync(userId, version.Value, Context.ConnectionAborted) is null)
            {
                connections.Remove(Context.ConnectionId);
                Context.Abort();
                return;
            }
            await base.OnConnectedAsync();
        }
        catch
        {
            connections.Remove(Context.ConnectionId);
            Context.Abort();
            throw;
        }
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
