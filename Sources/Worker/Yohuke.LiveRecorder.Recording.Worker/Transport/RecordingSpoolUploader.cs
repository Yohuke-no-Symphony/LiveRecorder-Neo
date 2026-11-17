using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Transport;

internal sealed class RecordingSpoolUploader(RecordingSpool spool, RecordingFileTransfer transfer,
    Func<WorkerConfiguration> configuration, Func<string, bool> captureStopped, Action<string> log,
    RecordingSessionProgressReporter? progress = null)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var assignment in spool.Assignments().Where(value => !value.Released))
            {
                try
                {
                    foreach (var session in spool.Sessions(assignment.Assignment.AssignmentId))
                    {
                        var sessionId = session.State.Descriptor.SessionId;
                        progress?.Uploading(sessionId);
                        try
                        {
                            await UploadSessionAsync(assignment.Assignment, session, cancellationToken);
                            progress?.Uploaded(sessionId);
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            progress?.Failed(sessionId);
                            throw;
                        }
                    }
                    if (!assignment.Assignment.Enabled && captureStopped(assignment.Assignment.AssignmentId)
                        && spool.Sessions(assignment.Assignment.AssignmentId).Count == 0)
                    {
                        var current = spool.Assignments().Single(value => value.Assignment.AssignmentId == assignment.Assignment.AssignmentId);
                        await transfer.StoppedAsync(current.Assignment, current.CompletedSessionIds, cancellationToken);
                        spool.SaveAssignment(current.Assignment, configuration().TempDirectory, released: true);
                        log("The server acknowledged all recording data and released the room assignment.");
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    log("Recording upload remains queued in the persistent spool (" + error.GetType().Name + ").");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task UploadSessionAsync(RecordingAssignment assignment, RecordingSpoolSession session, CancellationToken cancellationToken)
    {
        var receiptPath = Path.Combine(session.DirectoryPath, "uploaded.json");
        var receipt = File.Exists(receiptPath) ? RecordingSpoolFiles.Read<RecordingUploadReceipt>(receiptPath) : new();
        if (receipt.SessionClosed)
        {
            spool.DeleteAcknowledgedSession(session.DirectoryPath);
            return;
        }
        var commits = spool.Commits(session.DirectoryPath);
        if (!commits.Any(commit => commit.DurationMs > 0))
        {
            if (session.State.Descriptor.IsClosed)
            {
                spool.QuarantineDraft(session.DirectoryPath);
                log("A session without committed video was retained as a local draft; no empty broadcast was published.");
            }
            return;
        }
        await transfer.SessionAsync(assignment, session.State.Descriptor with { IsClosed = false, EndedAtUtc = null }, cancellationToken);
        foreach (var commit in commits)
        {
            if (!receipt.Segments.TryGetValue(commit.SegmentId, out var accepted) || accepted.CommitSequence < commit.CommitSequence
                || commit.IsClosed && !accepted.IsClosed)
            {
                accepted = await transfer.SegmentAsync(assignment, session.DirectoryPath, session.State.Descriptor.SessionId,
                    commit, configuration().UploadBytesPerSecond, cancellationToken,
                    value => progress?.Transfer(session.State.Descriptor.SessionId, value));
                receipt.Segments[commit.SegmentId] = accepted;
                RecordingSpoolFiles.Write(receiptPath, receipt);
            }
            if (commit.IsClosed && accepted.IsClosed)
            {
                DeleteClosedMedia(session.DirectoryPath, commit.RelativePath, commit.IndexRelativePath, commit.SegmentId);
            }
        }
        using (var events = spool.PinEvents(session.DirectoryPath))
        {
            var current = events.Session.State.Events;
            if (current is not null && (receipt.Events is null || receipt.Events.ThroughSequence < current.ThroughSequence
                    || receipt.Events.CommittedBytes < current.CommittedBytes || current.IsClosed && !receipt.Events.IsClosed))
            {
                receipt.Events = await transfer.EventsAsync(assignment, events.Session, configuration().UploadBytesPerSecond, cancellationToken,
                    value => progress?.Transfer(session.State.Descriptor.SessionId, value));
                RecordingSpoolFiles.Write(receiptPath, receipt);
            }
        }
        var state = RecordingSpoolFiles.Read<RecordingSessionState>(Path.Combine(session.DirectoryPath, "session.json"));
        var finalCommits = spool.Commits(session.DirectoryPath);
        if (state.Descriptor.IsClosed && finalCommits.All(commit => commit.IsClosed
                && receipt.Segments.TryGetValue(commit.SegmentId, out var accepted) && accepted.IsClosed
                && accepted.CommitSequence >= commit.CommitSequence)
            && state.Events is { IsClosed: true } finalEvents && receipt.Events is { IsClosed: true } acknowledgedEvents
            && acknowledgedEvents.CommittedBytes >= finalEvents.CommittedBytes)
        {
            progress?.Finalizing(state.Descriptor.SessionId);
            await transfer.SessionAsync(assignment, state.Descriptor, cancellationToken);
            receipt.SessionClosed = true;
            RecordingSpoolFiles.Write(receiptPath, receipt);
            spool.DeleteAcknowledgedSession(session.DirectoryPath);
            log("The server acknowledged a complete broadcast archive. Its local spool was removed.");
        }
    }

    private void DeleteClosedMedia(string directory, string media, string index, string segmentId)
    {
        try
        {
            File.Delete(RecordingSpoolFiles.Resolve(directory, media));
            File.Delete(RecordingSpoolFiles.Resolve(directory, index));
            File.Delete(Path.ChangeExtension(RecordingSpoolFiles.Resolve(directory, media), ".issues.txt"));
            var prefixes = Path.Combine(directory, "录制提交", RecordingSpoolFiles.SafeId(segmentId), "prefixes");
            if (Directory.Exists(prefixes))
            {
                Directory.Delete(prefixes, true);
            }
        }
        catch (IOException)
        {
            log("An acknowledged closed segment is still open locally; cleanup will retry.");
        }
    }
}
