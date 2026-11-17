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
    private readonly Action<string> log;
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
    private RecordingRoomSnapshot? roomSnapshot;
    private bool disposed;

    internal RecordingRoomRunner(RecordingAssignment assignment, RecordingSpool spool, IRecordingCaptureSource source,
        Func<WorkerConfiguration, IMediaRecorder> recorders, Func<WorkerConfiguration> configuration, TimeProvider time,
        Action<string> log, CancellationToken cancellationToken)
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
        session = spool.Sessions(assignment.AssignmentId).Where(value => !value.State.Descriptor.IsClosed)
            .MaxBy(value => value.State.Descriptor.StartedAtUtc);
        lastMediaAtUtc = session?.State.LastCapturedAtUtc;
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
                SessionId = session?.State.Descriptor.SessionId, LastCapturedAtUtc = lastMediaAtUtc, Room = roomSnapshot
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
        DateTime? offlineSince = null;
        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                var current = Current();
                if (!current.Enabled)
                {
                    break;
                }
                try
                {
                    if (!spool.HasCapacity(configuration(), session?.DirectoryPath))
                    {
                        SetState("StorageFull", "RecordingStorageFull");
                        await Task.Delay(TimeSpan.FromSeconds(1), time, lifetimeToken);
                        continue;
                    }
                    if (session is not null)
                    {
                        inactivity ??= new(TimeSpan.FromMinutes(current.RecordingTimeoutMinutes), time, lifetimeToken);
                        inactivity.Configure(TimeSpan.FromMinutes(current.RecordingTimeoutMinutes));
                    }
                    var token = inactivity?.Token ?? lifetimeToken;
                    var room = await source.GetRoomAsync(current, token);
                    RecordRoom(room);
                    if (!room.IsLive)
                    {
                        await StopEventsAsync();
                        offlineSince ??= time.GetUtcNow().UtcDateTime;
                        if (session is not null && time.GetUtcNow().UtcDateTime - offlineSince >= TimeSpan.FromMinutes(current.SessionGraceMinutes))
                        {
                            await CloseSessionAsync();
                            token = lifetimeToken;
                        }
                        SetState("Monitoring");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(current.MonitorIntervalSeconds, 1, 300)), time, token);
                        continue;
                    }
                    var graceExpired = offlineSince is { } offlineAt
                        && time.GetUtcNow().UtcDateTime - offlineAt >= TimeSpan.FromMinutes(current.SessionGraceMinutes);
                    var liveTime = room.LiveStartedAtUtc is { } start ? new DateTimeOffset(start).ToUnixTimeSeconds() : 0;
                    if (session is not null && (graceExpired || session.State.Descriptor.LiveTime > 0 && liveTime > 0
                        && session.State.Descriptor.LiveTime != liveTime))
                    {
                        await CloseSessionAsync();
                        token = lifetimeToken;
                    }
                    offlineSince = null;
                    session ??= spool.CreateSession(current, new()
                    {
                        SessionId = Guid.NewGuid().ToString("N"), RoomId = current.RoomId, LiveTime = liveTime,
                        StartedAtUtc = time.GetUtcNow().UtcDateTime, Title = room.Title,
                        StreamerName = room.Name, StreamerUid = room.Uid, CoverUrl = room.CoverUrl,
                        HasDanmu = current.RecordDanmu
                    }, configuration().TempDirectory);
                    inactivity ??= new(TimeSpan.FromMinutes(current.RecordingTimeoutMinutes), time, lifetimeToken);
                    token = inactivity.Token;
                    if (current.RecordDanmu && lastMediaAtUtc is not null && events.IsCompleted)
                    {
                        StartEvents(session);
                    }
                    var stream = await source.ResolveStreamAsync(current, token);
                    await RecordAttemptAsync(current, session, stream, token);
                    SetState("Retrying");
                }
                catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (inactivity is { IsExpired: true })
                {
                    await CloseSessionAsync();
                    offlineSince = null;
                    SetState("Monitoring");
                    log("The broadcast ended after the configured interval without valid media. Room monitoring continues.");
                }
                catch (Exception error)
                {
                    SetState("Retrying", "RecordingSourceUnavailable");
                    log("Recording source will reconnect (" + error.GetType().Name + ").");
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
                        spool.CloseSession(session.DirectoryPath, lastMediaAtUtc ?? time.GetUtcNow().UtcDateTime);
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
        progress.Activate(new RecordingProgress(update =>
        {
            spool.RecordMedia(activeSession.DirectoryPath, update);
            if (progress.HasMedia && update.Kind == "Committed" && update.CommitSnapshot is { DurationMs: > 0 } commit)
            {
                lock (sync)
                {
                    lastMediaAtUtc = activeSession.State.Descriptor.StartedAtUtc.AddMilliseconds(commit.StartOffsetMs + commit.DurationMs);
                    state = "Recording";
                    errorCode = null;
                    if (Current().RecordDanmu && events.IsCompleted)
                    {
                        StartEvents(activeSession);
                    }
                }
            }
        }));
        var recording = Task.CompletedTask;
        var monitor = Task.CompletedTask;
        try
        {
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
                var liveTime = room.LiveStartedAtUtc is { } liveAt ? new DateTimeOffset(liveAt).ToUnixTimeSeconds() : 0;
                if (!room.IsLive || activeSession.State.Descriptor.LiveTime > 0 && liveTime > 0
                    && liveTime != activeSession.State.Descriptor.LiveTime)
                {
                    return;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                log("Room monitoring is unavailable; the current media connection continues (" + error.GetType().Name + ").");
            }
        }
    }

    private void StartEvents(RecordingSpoolSession activeSession)
    {
        lock (sync)
        {
            if (!events.IsCompleted || cancellation.IsCancellationRequested || !assignment.RecordDanmu)
            {
                return;
            }
            eventsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            spool.UpdateSession(activeSession.DirectoryPath, value => value.Descriptor = value.Descriptor with { HasDanmu = true });
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
                await using var writer = new SessionEventWriter(Path.Combine(activeSession.DirectoryPath, "弹幕"),
                    commit => spool.RecordEventsAsync(activeSession.DirectoryPath, commit));
                await source.SubscribeEventsAsync(Current(), async (item, token) =>
                {
                    item.ReceivedAtUtc = item.ReceivedAtUtc == default ? time.GetUtcNow().UtcDateTime : item.ReceivedAtUtc;
                    item.OffsetMs = Math.Max(0, ((item.SourceTimeUtc ?? item.ReceivedAtUtc) - activeSession.State.Descriptor.StartedAtUtc).TotalMilliseconds);
                    await writer.WriteAsync(item, token);
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                log("The event archive will reconnect; committed events remain in the spool (" + error.GetType().Name + ").");
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

    private async Task CloseSessionAsync()
    {
        await StopEventsAsync();
        if (session is not null)
        {
            spool.CloseSession(session.DirectoryPath, lastMediaAtUtc ?? time.GetUtcNow().UtcDateTime);
            lock (sync)
            {
                session = null;
                lastMediaAtUtc = null;
            }
        }
        inactivity?.Dispose();
        inactivity = null;
    }

    private void SetState(string value, string? error = null)
    {
        lock (sync)
        {
            state = value;
            errorCode = error;
        }
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
