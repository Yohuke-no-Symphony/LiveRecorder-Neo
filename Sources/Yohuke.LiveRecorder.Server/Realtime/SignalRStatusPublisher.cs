using Microsoft.AspNetCore.SignalR;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Server.Realtime;

/// <summary>每次推送检查当前授权，私有任务只发给所有者和管理员。</summary>
public sealed class SignalRStatusPublisher(IHubContext<StatusHub> hub, StatusConnections connections, IIdentityService identity) : IStatusPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(string kind, string id, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        foreach (var connection in connections.All)
        {
            var user = await identity.ValidateSessionAsync(connection.UserId, connection.SessionVersion, cancellationToken);
            if (user is null)
            {
                connection.Abort();
                connections.Remove(connection.Id);
                continue;
            }
            if (ownerId is null && user.Role != "Admin" && !user.Permissions.Contains(PermissionIds.VIEW_RECORDINGS))
            {
                continue;
            }
            if (ownerId is null || ownerId == user.Id || user.Role == "Admin")
            {
                await hub.Clients.Client(connection.Id).SendAsync("status", new { kind, id }, cancellationToken);
            }
        }
    }
}
