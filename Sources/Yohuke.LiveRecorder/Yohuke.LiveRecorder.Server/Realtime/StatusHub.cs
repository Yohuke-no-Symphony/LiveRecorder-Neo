using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Yohuke.LiveRecorder.Server.Realtime;

/// <summary>面向已审核用户的状态更新通道。</summary>
public sealed class StatusHub(StatusConnections connections) : Hub
{
    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        connections.Add(new StatusConnection(Context.ConnectionId, Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!, Context.Abort));
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
