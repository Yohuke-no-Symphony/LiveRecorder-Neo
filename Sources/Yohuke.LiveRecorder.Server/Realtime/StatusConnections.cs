using System.Collections.Concurrent;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Server.Realtime;

/// <summary>仅保存当前在线连接，不缓存授权结论。</summary>
public sealed class StatusConnections : IUserSessionRevoker
{
    private readonly ConcurrentDictionary<string, StatusConnection> connections = new();
    public IReadOnlyCollection<StatusConnection> All => connections.Values.ToList();
    /// <summary>登记连接。</summary>
    public void Add(StatusConnection connection) => connections[connection.Id] = connection;
    /// <summary>移除断开的连接。</summary>
    public void Remove(string id) => connections.TryRemove(id, out _);

    /// <inheritdoc />
    public void Revoke(string userId, long sessionVersion)
    {
        foreach (var connection in connections.Values.Where(connection => connection.UserId == userId && connection.SessionVersion < sessionVersion))
        {
            if (connections.TryRemove(connection.Id, out var removed))
            {
                removed.Abort();
            }
        }
    }
}
