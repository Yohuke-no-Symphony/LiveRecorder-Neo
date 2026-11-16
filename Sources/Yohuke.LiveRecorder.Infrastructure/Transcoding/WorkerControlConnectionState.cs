namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

internal sealed record WorkerControlConnectionState(Guid ConnectionId, long LastHeartbeatTimestamp);
