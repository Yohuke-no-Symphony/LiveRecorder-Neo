using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Capture;

internal sealed class RecordingRoomRunner : IDisposable
{
    private readonly Lock sync = new();
    private readonly RecordingSpool spool;
    private readonly IRecordingCaptureSource source;
    private readonly Func<WorkerConfiguration, IMediaRecorder> recorders;
    private readonly Func<WorkerConfiguration> configuration;
    private readonly TimeProvider time;
    private readonly Action<WorkerLogEntry> log;
    private readonly CancellationTokenSource cancellation;
    private readonly Dictionary<CancellationTokenSource, Task> cancellationTasks = new();
    private RecordingAssignment assignment;
    private RecordingSpoolSession? session;
    private CancellationTokenSource? eventsCancellation;
    private Task events = Task.CompletedTask;
    private string state = "Monitoring";
    private string? errorCode;
    private DateTime? lastMediaAtUtc;
    private bool explicitStop;
    private CancellationTokenSource? activeAttempt;
    private RecordingInactivityMonitor? inactivity;
    private RecordingSessionLifecycle? lifecycle;
    private RecordingSessionContinuation? continuation;
    private RecordingSessionContinuation? lastClosedContinuation;
    private string? lastCapturedSessionId;
    private RecordingRoomSnapshot? roomSnapshot;
    private bool disposed;

    internal RecordingRoomRunner(RecordingAssignment assignment, RecordingSpool spool, IRecordingCaptureSource source,
        Func<WorkerConfiguration, IMediaRecorder> recorders, Func<WorkerConfiguration> configuration, TimeProvider time,
        Action<WorkerLogEntry> log, CancellationToken cancellationToken)
    {
        this.assignment = assignment;
        this.spool = spool;
        this.source = source;
        this.recorders = recorders;
        this.configuration = configuration;
        this.time = time;
        this.log = log;
        explicitStop = !assignment.Enabled;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCaptures = spool.Sessions(assignment.AssignmentId);
        session = previousCaptures.Where(value => !value.State.Descriptor.IsClosed)
            .MaxBy(value => value.State.Descriptor.StartedAtUtc);
        lastMediaAtUtc = session?.State.LastCapturedAtUtc;
        lastCapturedSessionId = previousCaptures.Where(value => value.State.LastCapturedAtUtc is not null)
            .MaxBy(value => value.State.Descriptor.StartedAtUtc)?.State.Descriptor.SessionId;
        continuation = spool.Assignments().FirstOrDefault(value => value.Assignment.AssignmentId == assignment.AssignmentId)?.LastContinuation
            ?? assignment.Continuation;
        if (session is not null)
        {
            continuation = new()
            {
                LogicalSessionId = string.IsNullOrEmpty(session.State.Descriptor.LogicalSessionId)
                    ? session.State.Descriptor.SessionId : session.State.Descriptor.LogicalSessionId,
                StartedAtUtc = session.State.Descriptor.LogicalStartedAtUtc ?? session.State.Descriptor.StartedAtUtc,
                Lifecycle = session.State.Descriptor.Lifecycle ?? new()
                {
                    LastMediaReceivedAtUtc = session.State.LastMediaReceivedAtUtc
                }
            };
            if (session.State.LastMediaReceivedAtUtc is { } received
                && (continuation.Lifecycle.Revision == 0 || continuation.Lifecycle.LastMediaReceivedAtUtc is null)
                && (continuation.Lifecycle.LastMediaReceivedAtUtc is null || received > continuation.Lifecycle.LastMediaReceivedAtUtc))
            {
                continuation = continuation with
                {
                    Lifecycle = continuation.Lifecycle with { LastMediaReceivedAtUtc = received }
                };
            }
        }
        else if (continuation?.Lifecycle.EndReason is { } ended && ended != RecordingSessionEndReason.None)
        {
            lastClosedContinuation = continuation;
            continuation = null;
        }
    }

    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal bool IsStopped => Completion.IsCompleted;

    internal void Start() => Completion = RunAsync();

    internal void Update(RecordingAssignment value)
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            var changed = assignment.ConfigurationRevision != value.ConfigurationRevision;
            assignment = value;
            if (changed)
            {
                if (activeAttempt is not null)
                {
                    CancelWhileLocked(activeAttempt);
                }
                if (eventsCancellation is not null)
                {
                    CancelWhileLocked(eventsCancellation);
                }
            }
            if (!value.Enabled && !explicitStop)
            {
                explicitStop = true;
                CancelWhileLocked(cancellation);
            }
        }
    }

    internal RecordingRoomProgress Snapshot()
    {
        lock (sync)
        {
            return new()
            {
                AssignmentId = assignment.AssignmentId, Generation = assignment.Generation, Token = assignment.Token,
                ConfigurationRevision = assignment.ConfigurationRevision, State = state,
                ErrorCode = errorCode,
                SessionId = lastCapturedSessionId, LastCapturedAtUtc = lastMediaAtUtc, Room = roomSnapshot,
                Continuation = continuation is not null ? continuation with { Lifecycle = lifecycle?.State ?? continuation.Lifecycle }
                    : lastClosedContinuation
            };
        }
    }

    private RecordingAssignment Current()
    {
        lock (sync)
        {
            return assignment;
        }
    }

    private async Task RunAsync()
    {
        var lifetimeToken = cancellation.Token;
        try
        {
            if (session is not null)
            {
                WriteCaptureLog(session, "Recovered the persisted broadcast session.");
            }
            while (!lifetimeToken.IsCancellationRequested)
            {
                var current = Current();
                if (!current.Enabled)
                {
                    break;
                }
                try
                {
                    if (continuation is not null)
                    {
                        lifecycle ??= new(continuation.Lifecycle, time, current.SessionGraceMinutes, current.RecordingTimeoutMinutes);
                        var recovered = lifecycle.State;
                        inactivity ??= new(TimeSpan.FromMinutes(current.RecordingTimeoutMinutes), time,
                            recovered.LastMediaReceivedAtUtc ?? recovered.TimeoutAnchorUtc, lifetimeToken);
                        inactivity.Configure(TimeSpan.FromMinutes(current.RecordingTimeoutMinutes));
                        if (lifecycle.Evaluate(current.SessionGraceMinutes, current.RecordingTimeoutMinutes) != RecordingSessionEndReason.None)
                        {
                            await CloseSessionAsync("The persisted broadcast deadline elapsed; the logical session is complete.");
                            SetState("Monitoring");
                            await Task.Delay(TimeSpan.FromSeconds(2), time, lifetimeToken);
                            continue;
                        }
                        PersistLifecycle();
                        if (session is not null && events.IsCompleted)
                        {
                            StartEvents(session);
                        }
                    }
                    var token = inactivity?.Token ?? lifetimeToken;
                    var room = await source.GetRoomAsync(current, token);
                    RecordRoom(room);
                    lock (sync)
                    {
                        lifecycle?.ObserveRoom(room.IsLive, GetLiveTime(room), time.GetUtcNow().UtcDateTime);
                        PersistLifecycle();
                    }
                    if (!room.IsLive)
                    {
                        if (lifecycle?.Evaluate() is { } ended && ended != RecordingSessionEndReason.None)
                        {
                            await CloseSessionAsync("Capture closed after the room remained offline beyond the grace interval.");
                            token = lifetimeToken;
                        }
                        if (session is not null && events.IsCompleted)
                        {
                            StartEvents(session);
                        }
                        SetState("Monitoring");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(current.MonitorIntervalSeconds, 1, 300)), time, token);
                        continue;
                    }
                    if (lifecycle?.Evaluate() is { } reason && reason != RecordingSessionEndReason.None)
                    {
                        await CloseSessionAsync("Capture closed at the logical broadcast deadline.");
                        token = lifetimeToken;
                    }
                    if (!spool.HasCapacity(configuration(), session?.DirectoryPath))
                    {
                        SetState("StorageFull", "RecordingStorageFull");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(current.MonitorIntervalSeconds, 1, 300)), time, token);
                        continue;
                    }
                    if (session is null)
                    {
                        var physicalId = Guid.NewGuid().ToString("N");
                        var now = time.GetUtcNow().UtcDateTime;
                        continuation ??= new()
                        {
                            LogicalSessionId = physicalId, StartedAtUtc = now,
                            Lifecycle = new() { TimeoutAnchorUtc = now, LiveTime = GetLiveTime(room) }
                        };
                        lifecycle ??= new(continuation.Lifecycle, time, current.SessionGraceMinutes, current.RecordingTimeoutMinutes);
                        session = spool.CreateSession(current, new()
                        {
                            SessionId = physicalId, RoomId = current.RoomId, LiveTime = GetLiveTime(room),
                            LogicalSessionId = continuation.LogicalSessionId, LogicalStartedAtUtc = continuation.StartedAtUtc,
                            Lifecycle = lifecycle.State, StartedAtUtc = now, Title = room.Title,
                            StreamerName = room.Name, StreamerUid = room.Uid, CoverUrl = room.CoverUrl,
                            HasDanmu = current.RecordDanmu
                        }, configuration().TempDirectory);
                        WriteCaptureLog(session, "Created a broadcast session; waiting for valid media.");
                    }
                    var activeLifecycle = lifecycle!.State;
                    inactivity ??= new(TimeSpan.FromMinutes(current.RecordingTimeoutMinutes), time,
                        activeLifecycle.LastMediaReceivedAtUtc ?? activeLifecycle.TimeoutAnchorUtc, lifetimeToken);
                    token = inactivity.Token;
                    if (events.IsCompleted)
                    {
                        StartEvents(session);
                    }
                    var stream = await source.ResolveStreamAsync(current, token);
                    await RecordAttemptAsync(current, session, stream, token);
                    SetState("Retrying");
                    WriteCaptureLog(session, "The capture connection ended; room monitoring continues before reconnecting.");
                }
                catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (inactivity is { IsExpired: true })
                {
                    lifecycle?.Evaluate();
                    await CloseSessionAsync(FormattableString.Invariant(
                        $"Capture ended after {current.RecordingTimeoutMinutes} minutes without valid media. Room monitoring continues."));
                    SetState("Monitoring");
                }
                catch (Exception error)
                {
                    SetState("Retrying", "RecordingSourceUnavailable");
                    WriteCaptureLog(session, "Recording source will reconnect (" + error.GetType().Name + ").", "Warning");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), time, lifetimeToken);
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        finally
        {
            Task callbacks;
            lock (sync)
            {
                callbacks = CancelWhileLocked(cancellation);
            }
            try
            {
                await Task.WhenAll(StopEventsAsync(), callbacks);
            }
            finally
            {
                try
                {
                    if (explicitStop && session is not null)
                    {
                        PersistLifecycle();
                        spool.CloseSession(session.DirectoryPath, lastMediaAtUtc ?? time.GetUtcNow().UtcDateTime);
                        WriteCaptureLog(session, "Capture stopped by the assignment; committed media and events remain queued for upload.");
                    }
                    else if (session is not null)
                    {
                        WriteCaptureLog(session, "Capture paused for worker shutdown; the open session remains available for recovery.");
                    }
                }
                finally
                {
                    inactivity?.Dispose();
                    inactivity = null;
                    SetState(explicitStop ? "Draining" : "Paused");
                }
            }
        }
    }

    private async Task RecordAttemptAsync(RecordingAssignment current, RecordingSpoolSession activeSession,
        LiveStreamDescriptor stream, CancellationToken cancellationToken)
    {
        var selected = configuration();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (sync)
        {
            activeAttempt = attempt;
        }
        var started = time.GetUtcNow().UtcDateTime;
        var progress = new RecordingAttemptProgress(inactivity!);
        var committedMedia = new Dictionary<string, (long Bytes, double Duration)>();
        progress.Activate(new RecordingProgress(update =>
        {
            spool.RecordMedia(activeSession.DirectoryPath, update);
            if (progress.HasMedia && update.Kind == "Committed" && update.CommitSnapshot is { DurationMs: > 0 } commit)
            {
                bool changed;
                bool resumed;
                lock (sync)
                {
                    var previous = committedMedia.GetValueOrDefault(update.SegmentId);
                    if (update.CommittedBytes > previous.Bytes && update.DurationMs > previous.Duration)
                    {
                        var received = update.LastMediaReceivedAtUtc ?? commit.LastMediaReceivedAtUtc ?? time.GetUtcNow().UtcDateTime;
                        lifecycle?.ReportMedia(received);
                        committedMedia[update.SegmentId] = (update.CommittedBytes, update.DurationMs);
                        PersistLifecycle();
                    }
                    changed = state != "Recording";
                    resumed = lastMediaAtUtc is not null;
                    lastMediaAtUtc = activeSession.State.Descriptor.StartedAtUtc.AddMilliseconds(commit.StartOffsetMs + commit.DurationMs);
                    lastCapturedSessionId = activeSession.State.Descriptor.SessionId;
                    state = "Recording";
                    errorCode = null;
                    if (events.IsCompleted)
                    {
                        StartEvents(activeSession);
                    }
                }
                if (changed)
                {
                    WriteCaptureLog(activeSession, resumed ? "Media capture resumed in the existing broadcast session."
                        : "Received the first committed media for the broadcast session.");
                }
            }
        }));
        var recording = Task.CompletedTask;
        var monitor = Task.CompletedTask;
        try
        {
            WriteCaptureLog(activeSession, "Opening a capture connection.");
            recording = recorders(selected).RecordAsync(new()
            {
                SessionId = activeSession.State.Descriptor.SessionId, AttemptId = Guid.NewGuid().ToString("N"),
                RoomId = current.RoomId, StreamUrl = stream.Url, Headers = stream.Headers.ToDictionary(),
                OutputDirectory = activeSession.DirectoryPath, Title = activeSession.State.Descriptor.Title,
                MaxSegmentBytes = RecordingSegmentPolicy.MaximumBytes(selected, current),
                StartedAtUtc = started, SessionStartedAtUtc = activeSession.State.Descriptor.StartedAtUtc,
                FileTimeZoneId = current.TimeZone, CaptureCommitSnapshots = true
            }, progress, attempt.Token);
            monitor = MonitorAttemptAsync(activeSession, attempt.Token);
            await Task.WhenAny(recording, monitor);
        }
        finally
        {
            Task callbacks;
            lock (sync)
            {
                activeAttempt = null;
                callbacks = CancelWhileLocked(attempt);
            }
            try
            {
                await Task.WhenAll(recording, monitor, callbacks);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                lock (sync)
                {
                    cancellationTasks.Remove(attempt);
                }
            }
        }
    }

    private async Task MonitorAttemptAsync(RecordingSpoolSession activeSession, CancellationToken cancellationToken)
    {
        var nextRoomCheck = time.GetUtcNow();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken);
            var current = Current();
            if (lifecycle?.Evaluate(current.SessionGraceMinutes, current.RecordingTimeoutMinutes) is { } ended
                && ended != RecordingSessionEndReason.None)
            {
                return;
            }
            if (!spool.HasCapacity(configuration(), activeSession.DirectoryPath))
            {
                SetState("StorageFull", "RecordingStorageFull");
                return;
            }
            if (time.GetUtcNow() < nextRoomCheck)
            {
                continue;
            }
            nextRoomCheck = time.GetUtcNow().AddSeconds(Math.Clamp(current.MonitorIntervalSeconds, 1, 300));
            try
            {
                var room = await source.GetRoomAsync(current, cancellationToken);
                RecordRoom(room);
                lock (sync)
                {
                    lifecycle?.ObserveRoom(room.IsLive, GetLiveTime(room), time.GetUtcNow().UtcDateTime);
                    PersistLifecycle();
                }
                if (!room.IsLive)
                {
                    return;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                WriteCaptureLog(activeSession, "Room monitoring is unavailable; the current media connection continues (" + error.GetType().Name + ").", "Warning");
            }
        }
    }

    private void StartEvents(RecordingSpoolSession activeSession)
    {
        lock (sync)
        {
            if (!events.IsCompleted || cancellation.IsCancellationRequested)
            {
                return;
            }
            eventsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            if (assignment.RecordDanmu)
            {
                spool.UpdateSession(activeSession.DirectoryPath, value => value.Descriptor = value.Descriptor with { HasDanmu = true });
            }
            events = RunEventsAsync(activeSession, eventsCancellation);
        }
    }

    private async Task RunEventsAsync(RecordingSpoolSession activeSession, CancellationTokenSource selected)
    {
        try
        {
            await CaptureEventsAsync(activeSession, selected.Token);
        }
        finally
        {
            Task callbacks;
            lock (sync)
            {
                if (eventsCancellation == selected)
                {
                    eventsCancellation = null;
                }
                callbacks = CancelWhileLocked(selected);
            }
            try
            {
                await callbacks;
            }
            finally
            {
                lock (sync)
                {
                    cancellationTasks.Remove(selected);
                    selected.Dispose();
                }
            }
        }
    }

    private async Task CaptureEventsAsync(RecordingSpoolSession activeSession, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                WriteCaptureLog(activeSession, "Opening an event capture connection.");
                await using var writer = Current().RecordDanmu
                    ? new SessionEventWriter(Path.Combine(activeSession.DirectoryPath, "弹幕"), commit => spool.RecordEventsAsync(activeSession.DirectoryPath, commit))
                    : null;
                await source.SubscribeEventsAsync(Current(), async (item, token) =>
                {
                    item.ReceivedAtUtc = item.ReceivedAtUtc == default ? time.GetUtcNow().UtcDateTime : item.ReceivedAtUtc;
                    if (item.Kind == "RoomState" && item.Command is "LIVE" or "PREPARING")
                    {
                        lock (sync)
                        {
                            var before = lifecycle?.State;
                            lifecycle?.ObserveSignal(item.Command, item.ReceivedAtUtc);
                            var observed = lifecycle?.State;
                            PersistLifecycle();
                            if (item.Command == "PREPARING" && observed?.LastObservedIsLive == false
                                && observed.LastObservationAtUtc == item.ReceivedAtUtc
                                && before?.LastObservationAtUtc != observed.LastObservationAtUtc)
                            {
                                if (activeAttempt is not null)
                                {
                                    CancelWhileLocked(activeAttempt);
                                }
                            }
                        }
                    }
                    item.OffsetMs = Math.Max(0, ((item.SourceTimeUtc ?? item.ReceivedAtUtc) - activeSession.State.Descriptor.StartedAtUtc).TotalMilliseconds);
                    if (writer is not null)
                    {
                        await writer.WriteAsync(item, token);
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                WriteCaptureLog(activeSession, "The event archive will reconnect; committed events remain in the spool (" + error.GetType().Name + ").", "Warning");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken);
        }
    }

    private async Task StopEventsAsync()
    {
        Task pending;
        Task callbacks;
        lock (sync)
        {
            pending = events;
            callbacks = eventsCancellation is { } selected ? CancelWhileLocked(selected) : Task.CompletedTask;
        }
        try
        {
            await Task.WhenAll(pending, callbacks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task CancelWhileLocked(CancellationTokenSource source)
    {
        if (!cancellationTasks.TryGetValue(source, out var pending))
        {
            pending = source.CancelAsync();
            cancellationTasks.Add(source, pending);
        }
        return pending;
    }

    private async Task CloseSessionAsync(string message)
    {
        await StopEventsAsync();
        RecordingInactivityMonitor? monitor;
        lock (sync)
        {
            PersistLifecycle();
            lastClosedContinuation = continuation;
            if (session is not null)
            {
                spool.CloseSession(session.DirectoryPath, lastMediaAtUtc ?? time.GetUtcNow().UtcDateTime);
                WriteCaptureLog(session, message);
                session = null;
                lastMediaAtUtc = null;
            }
            monitor = inactivity;
            inactivity = null;
            lifecycle = null;
            continuation = null;
        }
        monitor?.Dispose();
    }

    private void PersistLifecycle()
    {
        lock (sync)
        {
            if (lifecycle is null || continuation is null)
            {
                return;
            }
            var snapshot = lifecycle.State;
            continuation = continuation with { Lifecycle = snapshot };
            if (session is not null)
            {
                spool.UpdateSession(session.DirectoryPath, value =>
                {
                    value.Descriptor = value.Descriptor with { Lifecycle = snapshot };
                    value.LastMediaReceivedAtUtc = snapshot.LastMediaReceivedAtUtc;
                });
            }
            spool.SaveContinuation(assignment.AssignmentId, continuation);
        }
    }

    private static long GetLiveTime(LiveRoomSnapshot room) => room.LiveStartedAtUtc is { } started
        ? new DateTimeOffset(started).ToUnixTimeSeconds() : 0;

    private void SetState(string value, string? error = null)
    {
        RecordingSpoolSession? selected;
        bool changed;
        lock (sync)
        {
            changed = state != value || errorCode != error;
            state = value;
            errorCode = error;
            selected = session;
        }
        if (changed && value == "StorageFull")
        {
            WriteCaptureLog(selected, "Capture is waiting for available spool capacity; committed data remain preserved.", "Warning");
        }
    }

    private void WriteCaptureLog(RecordingSpoolSession? selected, string message, string level = "Information")
    {
        log(new()
        {
            TimestampUtc = time.GetUtcNow().UtcDateTime, JobId = selected?.State.Descriptor.SessionId,
            Stage = selected is null ? null : "Capturing", Level = level, Message = message
        });
    }

    private void RecordRoom(LiveRoomSnapshot value)
    {
        lock (sync)
        {
            roomSnapshot = new()
            {
                RoomId = value.RoomId, Uid = value.Uid, Name = value.Name, AvatarUrl = value.AvatarUrl,
                IsLive = value.IsLive, CheckedAtUtc = time.GetUtcNow().UtcDateTime
            };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            cancellation.Dispose();
        }
    }
}
