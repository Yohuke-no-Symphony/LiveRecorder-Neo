namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>Tracks each authenticated control connection independently using monotonic heartbeat time.</summary>
public sealed class WorkerConnectionStatusTracker(TimeProvider? timeProvider = null)
{
    private const int HEARTBEAT_TIMEOUT_SECONDS = 45;
    private readonly Lock gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, WorkerControlConnectionState> connections = new(StringComparer.Ordinal);

    /// <summary>Registers a new generation without inheriting the lifecycle of its previous connection.</summary>
    public Guid Open(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var id = Guid.NewGuid();
        lock (gate)
        {
            connections[workerId] = new(id, clock.GetTimestamp());
        }
        return id;
    }

    /// <summary>Refreshes only the connection generation that actually completed an authenticated heartbeat.</summary>
    public bool Heartbeat(string workerId, Guid connectionId)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(workerId, out var current) || current.ConnectionId != connectionId)
            {
                return false;
            }
            connections[workerId] = current with { LastHeartbeatTimestamp = clock.GetTimestamp() };
            return true;
        }
    }

    /// <summary>Marks a closed connection offline while leaving any replacement generation intact.</summary>
    public void Close(string workerId, Guid connectionId)
    {
        lock (gate)
        {
            if (connections.TryGetValue(workerId, out var current) && current.ConnectionId == connectionId)
            {
                connections.Remove(workerId);
            }
        }
    }

    /// <summary>Checks connection ownership independently from heartbeat age before dispatch mutates tasks.</summary>
    public bool IsCurrent(string workerId, Guid connectionId)
    {
        lock (gate)
        {
            return connections.TryGetValue(workerId, out var current) && current.ConnectionId == connectionId;
        }
    }

    /// <summary>Returns server-authoritative liveness without relying on device identity or client wall clocks.</summary>
    public bool IsOnline(string workerId)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(workerId, out var current))
            {
                return false;
            }
            var elapsed = clock.GetElapsedTime(current.LastHeartbeatTimestamp);
            return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(HEARTBEAT_TIMEOUT_SECONDS);
        }
    }
}
