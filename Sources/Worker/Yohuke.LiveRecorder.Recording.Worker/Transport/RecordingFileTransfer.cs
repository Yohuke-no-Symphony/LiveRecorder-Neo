using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Recording.Worker.Transport;

internal sealed class RecordingFileTransfer : IDisposable
{
    private readonly WorkerTransferClient client;

    internal RecordingFileTransfer(WorkerConnectionOptions options, HttpMessageHandler? handler = null)
    {
        client = new(options, "api/worker/recording/targets", "X-Worker-Generation", "X-Worker-Token", handler);
    }

    internal Task<RecordingAssignment> AssignmentAsync(RecordingLease lease, CancellationToken cancellationToken) =>
        client.GetJsonAsync<RecordingAssignment>(Lease(lease), "assignment", cancellationToken);

    internal Task SessionAsync(RecordingAssignment assignment, RecordingSessionDescriptor descriptor, CancellationToken cancellationToken) =>
        client.PostJsonAsync(Lease(assignment), "sessions", descriptor, cancellationToken);

    internal async Task<RecordingCommitAcknowledgement> SegmentAsync(RecordingAssignment assignment, string directory,
        string sessionId, MediaRecordingCommitSnapshot snapshot, long bytesPerSecond, CancellationToken cancellationToken)
    {
        var lease = Lease(assignment);
        var endpoint = "sessions/" + Uri.EscapeDataString(sessionId) + "/segments/" + Uri.EscapeDataString(snapshot.SegmentId);
        await client.AppendFileAsync(lease, RecordingSpoolFiles.Resolve(directory, snapshot.RelativePath), endpoint + "/media",
            snapshot.MediaCommittedBytes, bytesPerSecond, cancellationToken);
        await client.AppendFileAsync(lease, RecordingSpoolFiles.Resolve(directory, snapshot.IndexRelativePath), endpoint + "/index",
            snapshot.IndexCommittedBytes, bytesPerSecond, cancellationToken);
        await client.UploadFileAsync(lease, RecordingSpoolFiles.Resolve(directory, snapshot.PrefixRelativePath),
            endpoint + "/prefixes/" + snapshot.PrefixSha256, snapshot.PrefixLength, bytesPerSecond, cancellationToken);
        var commit = new RecordingSegmentCommit
        {
            SessionId = sessionId, SegmentId = snapshot.SegmentId,
            RelativePath = snapshot.RelativePath, IndexRelativePath = snapshot.IndexRelativePath,
            CommitSequence = snapshot.CommitSequence, MediaCommittedBytes = snapshot.MediaCommittedBytes,
            IndexCommittedBytes = snapshot.IndexCommittedBytes, PrefixLength = snapshot.PrefixLength,
            PrefixSha256 = snapshot.PrefixSha256, StartOffsetMs = snapshot.StartOffsetMs,
            DurationMs = snapshot.DurationMs, Codec = snapshot.Codec, IsClosed = snapshot.IsClosed
        };
        var acknowledgement = await client.PostJsonResponseAsync<RecordingSegmentCommit, RecordingCommitAcknowledgement>(
            lease, endpoint + "/commit", commit, cancellationToken);
        if (acknowledgement.CommitSequence < snapshot.CommitSequence || acknowledgement.MediaCommittedBytes < snapshot.MediaCommittedBytes
            || acknowledgement.IndexCommittedBytes < snapshot.IndexCommittedBytes || snapshot.IsClosed && !acknowledgement.IsClosed)
        {
            throw new InvalidDataException("The recording server acknowledged an incomplete media commit.");
        }
        return acknowledgement;
    }

    internal async Task<RecordingEventAcknowledgement> EventsAsync(RecordingAssignment assignment, RecordingSpoolSession session,
        long bytesPerSecond, CancellationToken cancellationToken)
    {
        var commit = session.State.Events ?? throw new InvalidDataException("Missing recording event commit.");
        var lease = Lease(assignment);
        var endpoint = "sessions/" + Uri.EscapeDataString(session.State.Descriptor.SessionId);
        if (commit.CommittedBytes > 0)
        {
            await client.AppendFileAsync(lease, Path.Combine(session.DirectoryPath, "弹幕", "事件.jsonl"), endpoint + "/events",
                commit.CommittedBytes, bytesPerSecond, cancellationToken);
        }
        if (commit.IndexCommittedBytes > 0)
        {
            await client.UploadFileAsync(lease, RecordingSpoolFiles.Resolve(session.DirectoryPath, session.State.EventIndexPath!),
                endpoint + "/event-indexes/" + commit.IndexSha256, commit.IndexCommittedBytes, bytesPerSecond, cancellationToken);
        }
        var acknowledgement = await client.PostJsonResponseAsync<RecordingEventCommit, RecordingEventAcknowledgement>(lease,
            endpoint + "/events/commit", commit, cancellationToken);
        if (acknowledgement.CommittedBytes < commit.CommittedBytes || acknowledgement.IndexCommittedBytes < commit.IndexCommittedBytes
            || acknowledgement.ThroughSequence < commit.ThroughSequence || commit.IsClosed && !acknowledgement.IsClosed)
        {
            throw new InvalidDataException("The recording server acknowledged an incomplete event commit.");
        }
        return acknowledgement;
    }

    internal Task StoppedAsync(RecordingAssignment assignment, IReadOnlyCollection<string> sessions, CancellationToken cancellationToken) =>
        client.PostJsonAsync(Lease(assignment), "stopped", new RecordingStopAcknowledgement
        {
            SessionIds = [.. sessions], PendingUploadBytes = 0
        }, cancellationToken);

    private static WorkerTransferLease Lease(RecordingAssignment assignment) => new()
    {
        JobId = assignment.AssignmentId, Attempt = assignment.Generation, LeaseToken = assignment.Token
    };

    private static WorkerTransferLease Lease(RecordingLease lease) => new()
    {
        JobId = lease.AssignmentId, Attempt = lease.Generation, LeaseToken = lease.Token
    };

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
