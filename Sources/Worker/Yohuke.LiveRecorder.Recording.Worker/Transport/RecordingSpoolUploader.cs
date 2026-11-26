using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Recording.Worker.Transport;

internal sealed class RecordingSpoolUploader(RecordingSpool spool, RecordingFileTransfer transfer,
    Func<WorkerConfiguration> configuration, Func<string, bool> captureStopped, Action<WorkerLogEntry> log,
    RecordingSessionProgressReporter? progress = null, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan LEASE_RETRY_DELAY = TimeSpan.FromSeconds(30);
    private readonly Lock uploadSync = new();
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<(string AssignmentId, string? SessionId), (long FailedAt, string Message)> leaseRejections = new();
    private readonly Dictionary<RecordingUploadAbandonmentCommand, (long FailedAt, string Message)> abandonmentRejections = new();
    private (RecordingAssignment Assignment, string SessionId, CancellationTokenSource Cancellation)? activeUpload;
    private Task stopUpload = Task.CompletedTask;

    internal void AcceptAbandonments(IReadOnlyList<RecordingUploadAbandonmentCommand> commands)
    {
        if (commands.Count == 0)
        {
            return;
        }
        lock (uploadSync)
        {
            spool.SaveAbandonments(commands);
            if (activeUpload is { } upload && !upload.Cancellation.IsCancellationRequested
                && spool.HasAbandonment(upload.Assignment.AssignmentId, upload.Assignment.Generation, upload.SessionId))
            {
                stopUpload = upload.Cancellation.CancelAsync();
            }
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ProcessAbandonmentsAsync(cancellationToken);
            foreach (var assignment in spool.Assignments().Where(value => !value.Released))
            {
                var assignmentId = assignment.Assignment.AssignmentId;
                var stopScope = (assignmentId, (string?)null);
                try
                {
                    var deferredBroadcasts = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var session in spool.Sessions(assignmentId).Where(value => value.State.Descriptor.IsClosed)
                                 .OrderBy(value => value.State.Descriptor.StartedAtUtc))
                    {
                        var sessionId = session.State.Descriptor.SessionId;
                        var logicalId = session.State.Descriptor.LogicalSessionId ?? sessionId;
                        var scope = (assignmentId, sessionId);
                        if (deferredBroadcasts.Contains(logicalId) || !LeaseRetryIsDue(scope))
                        {
                            deferredBroadcasts.Add(logicalId);
                            continue;
                        }
                        try
                        {
                            if (!await UploadUnlessAbandonedAsync(assignment.Assignment, session, cancellationToken))
                            {
                                deferredBroadcasts.Add(logicalId);
                                continue;
                            }
                            leaseRejections.Remove(scope);
                            progress?.Uploaded(sessionId);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            WriteUploadLog(sessionId, "Upload paused; unacknowledged recording data remain queued.");
                            throw;
                        }
                        catch (WorkerLeaseLostException error) when (!cancellationToken.IsCancellationRequested)
                        {
                            progress?.Failed(sessionId);
                            deferredBroadcasts.Add(logicalId);
                            if (RememberLeaseRejection(scope, error))
                            {
                                WriteUploadLog(sessionId, "Recording upload remains queued after a server rejection. " + error.Message, "Warning");
                            }
                        }
                        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                        {
                            progress?.Failed(sessionId);
                            deferredBroadcasts.Add(logicalId);
                            leaseRejections.Remove(scope);
                            WriteUploadLog(sessionId,
                                $"Recording upload will retry. Committed data remain in the spool ({error.GetType().Name}). {error.Message}", "Warning");
                        }
                    }
                    if (!assignment.Assignment.Enabled && captureStopped(assignmentId) && LeaseRetryIsDue(stopScope)
                        && !spool.HasAbandonment(assignmentId, assignment.Assignment.Generation)
                        && spool.Sessions(assignmentId).Count == 0)
                    {
                        var current = spool.Assignments().Single(value => value.Assignment.AssignmentId == assignmentId);
                        await transfer.StoppedAsync(current.Assignment, current.CompletedSessionIds, cancellationToken);
                        spool.SaveAssignment(current.Assignment, configuration().TempDirectory, released: true);
                        leaseRejections.Remove(stopScope);
                        WriteNodeLog("The server acknowledged all recording data and released the room assignment.");
                    }
                }
                catch (WorkerLeaseLostException error) when (!cancellationToken.IsCancellationRequested)
                {
                    if (RememberLeaseRejection(stopScope, error))
                    {
                        WriteNodeLog("The recording assignment remains queued after a server rejection. " + error.Message, "Warning");
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    leaseRejections.Remove(stopScope);
                    WriteNodeLog($"Recording upload remains queued in the persistent spool ({error.GetType().Name}). {error.Message}", "Warning");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken);
        }
    }

    private async Task ProcessAbandonmentsAsync(CancellationToken cancellationToken)
    {
        foreach (var command in spool.Abandonments())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (abandonmentRejections.TryGetValue(command, out var previous)
                && time.GetElapsedTime(previous.FailedAt) < LEASE_RETRY_DELAY)
            {
                continue;
            }
            try
            {
                spool.AbandonSession(command);
                await transfer.AcknowledgeAbandonmentAsync(command, cancellationToken);
                spool.ConfirmAbandonment(command);
                abandonmentRejections.Remove(command);
                leaseRejections.Remove((command.AssignmentId, command.SessionId));
                leaseRejections.Remove((command.AssignmentId, null));
                progress?.Uploaded(command.SessionId);
                WriteUploadLog(command.SessionId, "The server acknowledged that the abandoned recording is absent from the local spool.");
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                abandonmentRejections[command] = (time.GetTimestamp(), error.Message);
                if (previous.Message != error.Message)
                {
                    WriteUploadLog(command.SessionId, "Recording upload abandonment remains queued. " + error.Message, "Warning");
                }
            }
        }
    }

    private async Task<bool> UploadUnlessAbandonedAsync(RecordingAssignment assignment, RecordingSpoolSession session,
        CancellationToken cancellationToken)
    {
        var sessionId = session.State.Descriptor.SessionId;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (uploadSync)
        {
            if (spool.HasAbandonment(assignment.AssignmentId, assignment.Generation, sessionId))
            {
                return false;
            }
            activeUpload = (assignment, sessionId, attempt);
        }
        try
        {
            progress?.Uploading(sessionId);
            await UploadSessionAsync(assignment, session, attempt.Token);
            return true;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            Task callbacks;
            lock (uploadSync)
            {
                activeUpload = null;
                callbacks = stopUpload;
                stopUpload = Task.CompletedTask;
            }
            await callbacks;
        }
    }

    private bool LeaseRetryIsDue((string AssignmentId, string? SessionId) scope) =>
        !leaseRejections.TryGetValue(scope, out var previous) || time.GetElapsedTime(previous.FailedAt) >= LEASE_RETRY_DELAY;

    private bool RememberLeaseRejection((string AssignmentId, string? SessionId) scope, WorkerLeaseLostException error)
    {
        var changed = !leaseRejections.TryGetValue(scope, out var previous) || previous.Message != error.Message;
        leaseRejections[scope] = (time.GetTimestamp(), error.Message);
        return changed;
    }

    private async Task UploadSessionAsync(RecordingAssignment assignment, RecordingSpoolSession session, CancellationToken cancellationToken)
    {
        var receiptPath = Path.Combine(session.DirectoryPath, "uploaded.json");
        var receipt = File.Exists(receiptPath) ? RecordingSpoolFiles.Read<RecordingUploadReceipt>(receiptPath) : new();
        if (receipt.SessionClosed)
        {
            spool.DeleteAcknowledgedSession(session.DirectoryPath);
            WriteUploadLog(session.State.Descriptor.SessionId, "Recovered the final server acknowledgement and removed the completed local archive.");
            return;
        }
        var commits = spool.Commits(session.DirectoryPath);
        if (!commits.Any(commit => commit.DurationMs > 0))
        {
            if (session.State.Descriptor.IsClosed)
            {
                spool.QuarantineDraft(session.DirectoryPath);
                WriteUploadLog(session.State.Descriptor.SessionId, "A session without committed video was retained as a local draft; no empty broadcast was published.");
            }
            return;
        }
        await transfer.SessionAsync(assignment, session.State.Descriptor with { IsClosed = false, EndedAtUtc = null }, cancellationToken);
        foreach (var commit in commits)
        {
            if (!receipt.Segments.TryGetValue(commit.SegmentId, out var accepted) || accepted.CommitSequence < commit.CommitSequence
                || commit.IsClosed && !accepted.IsClosed)
            {
                WriteUploadLog(session.State.Descriptor.SessionId, FormattableString.Invariant(
                    $"Uploading media segment {commit.SegmentId}, commit {commit.CommitSequence}."));
                accepted = await transfer.SegmentAsync(assignment, session.DirectoryPath, session.State.Descriptor.SessionId,
                    commit, configuration().UploadBytesPerSecond, cancellationToken,
                    value => progress?.Transfer(session.State.Descriptor.SessionId, value));
                receipt.Segments[commit.SegmentId] = accepted;
                RecordingSpoolFiles.Write(receiptPath, receipt);
                WriteUploadLog(session.State.Descriptor.SessionId, FormattableString.Invariant(
                    $"The server acknowledged media segment {commit.SegmentId}, commit {accepted.CommitSequence}, {accepted.MediaCommittedBytes} media bytes and {accepted.IndexCommittedBytes} index bytes."));
            }
            if (commit.IsClosed && accepted.IsClosed)
            {
                DeleteClosedMedia(session.State.Descriptor.SessionId, session.DirectoryPath, commit.RelativePath, commit.IndexRelativePath, commit.SegmentId);
            }
        }
        using (var events = spool.PinEvents(session.DirectoryPath))
        {
            var current = events.Session.State.Events;
            if (current is not null && (receipt.Events is null || receipt.Events.ThroughSequence < current.ThroughSequence
                    || receipt.Events.CommittedBytes < current.CommittedBytes || current.IsClosed && !receipt.Events.IsClosed))
            {
                WriteUploadLog(session.State.Descriptor.SessionId, FormattableString.Invariant(
                    $"Uploading event archive through sequence {current.ThroughSequence}."));
                receipt.Events = await transfer.EventsAsync(assignment, events.Session, configuration().UploadBytesPerSecond, cancellationToken,
                    value => progress?.Transfer(session.State.Descriptor.SessionId, value));
                RecordingSpoolFiles.Write(receiptPath, receipt);
                WriteUploadLog(session.State.Descriptor.SessionId, FormattableString.Invariant(
                    $"The server acknowledged event sequence {receipt.Events.ThroughSequence} and {receipt.Events.CommittedBytes} archive bytes."));
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
            WriteUploadLog(state.Descriptor.SessionId, "All media and event commits are acknowledged; requesting final archive confirmation.");
            await transfer.SessionAsync(assignment, state.Descriptor, cancellationToken);
            receipt.SessionClosed = true;
            RecordingSpoolFiles.Write(receiptPath, receipt);
            spool.DeleteAcknowledgedSession(session.DirectoryPath);
            WriteUploadLog(state.Descriptor.SessionId, "The server acknowledged the complete broadcast archive. Its local spool was removed.");
        }
    }

    private void DeleteClosedMedia(string sessionId, string directory, string media, string index, string segmentId)
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
            WriteUploadLog(sessionId, "An acknowledged closed segment is still open locally; cleanup will retry.", "Warning");
        }
    }

    private void WriteUploadLog(string sessionId, string message, string level = "Information") => log(new()
    {
        TimestampUtc = DateTime.UtcNow, JobId = sessionId, Stage = "Uploading", Level = level, Message = message
    });

    private void WriteNodeLog(string message, string level = "Information") => log(new()
    {
        TimestampUtc = DateTime.UtcNow, Level = level, Message = message
    });
}
