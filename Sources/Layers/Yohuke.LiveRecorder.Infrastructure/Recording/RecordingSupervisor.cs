using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Infrastructure.Tasks;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>并行维护启用的直播间，数据库和派生任务不进入采集热路径。</summary>
public sealed class RecordingSupervisor(IMongoContext context, IBootstrapStore bootstrap, ISettingsStore settingsStore, IBilibiliService bilibili, IMediaRecorder recorder, IStatusPublisher publisher, ILogger<RecordingSupervisor> logger, SessionGroupingService grouping, SessionSegmentTrashService segmentTrash, IAppPaths paths, TimeProvider? timeProvider = null, RecordingOwnershipService? ownership = null, RecordingHandoffService? handoff = null, TaskEventLogStore? taskLogs = null,
    RecordingSessionProjectionService? projection = null) : BackgroundService
{
    private readonly Dictionary<string, RecordingWorkerHandle> workers = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly string localOwnerId = "local-" + TranscodeAssetRegistry.Hash(bootstrap.InstallationToken + ":" + Environment.MachineName);
    private readonly RecordingHandoffService handoffs = handoff ?? new(context, paths, publisher);
    private readonly RecordingSessionProjectionService projections = projection ?? new(context, paths, settingsStore, publisher, timeProvider);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconciled = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (bootstrap.IsReady)
                {
                    if (!reconciled)
                    {
                        await ReconcileAsync(stoppingToken);
                        reconciled = true;
                    }
                    var targets = await context.Collection<Streamer>("streamers").Find(x => x.Enabled && x.IsMonitored).ToListAsync(stoppingToken);
                    var ids = targets.Select(x => x.Id).ToHashSet();
                    var yielding = new HashSet<string>();
                    if (ownership is not null)
                    {
                        foreach (var target in targets.Where(value => workers.ContainsKey(value.Id)))
                        {
                            if (await ownership.TryRequestHandoffAsync(target.RoomId, localOwnerId, stoppingToken))
                            {
                                yielding.Add(target.Id);
                            }
                        }
                    }
                    foreach (var item in workers.Where(x => !ids.Contains(x.Key) || x.Value.Task.IsCompleted || yielding.Contains(x.Key)).ToList())
                    {
                        await item.Value.Cancellation.CancelAsync();
                        await ObserveAsync(item.Value.Task);
                        if (!ids.Contains(item.Key))
                        {
                            await context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == item.Key && !x.Enabled, Builders<Streamer>.Update.Set(x => x.State, "Stopped"), cancellationToken: stoppingToken);
                        }
                        item.Value.Cancellation.Dispose();
                        workers.Remove(item.Key);
                    }
                    foreach (var target in targets.Where(x => !workers.ContainsKey(x.Id)))
                    {
                        if (ownership is not null && await ownership.FindReadyWorkerAsync(stoppingToken) is not null)
                        {
                            var reserved = await ownership.GetAsync(target.RoomId, stoppingToken);
                            if (reserved is null || reserved.State == "Released" || reserved.OwnerKind != "Local" || reserved.OwnerId != localOwnerId)
                            {
                                continue;
                            }
                        }
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        workers[target.Id] = new RecordingWorkerHandle(cancellation, TrackOwnedAsync(target, cancellation.Token));
                    }
                    await projections.MaintainPausedAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("Recording target synchronization is deferred: {Message}", error.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        foreach (var worker in workers.Values)
        {
            await worker.Cancellation.CancelAsync();
        }
        await Task.WhenAll(workers.Values.Select(x => ObserveAsync(x.Task)));
        foreach (var worker in workers.Values)
        {
            worker.Cancellation.Dispose();
        }
    }

    private async Task TrackOwnedAsync(Streamer target, CancellationToken cancellationToken)
    {
        if (ownership is null)
        {
            await TrackAsync(target.Id, cancellationToken);
            return;
        }
        var lockPath = MediaPaths.Resolve(paths.RecordsDirectory, Path.Combine(".work", "recording-capture", target.RoomId + ".lock"));
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream captureLock;
        try
        {
            captureLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return;
        }
        await using (captureLock)
        {
            var assignment = await ownership.TryAcquireLocalAsync(target, localOwnerId, cancellationToken);
            if (assignment is null)
            {
                return;
            }
            try
            {
                await TrackAsync(target.Id, cancellationToken);
            }
            finally
            {
                var room = await ownership.GetAsync(target.RoomId, CancellationToken.None);
                if (room?.HandoffWorkerId is not null)
                {
                    await handoffs.CloseLocalSessionsAsync(target.Id, CancellationToken.None);
                }
                await ownership.ReleaseLocalAsync(assignment, CancellationToken.None);
            }
        }
    }

    private async Task TrackAsync(string id, CancellationToken cancellationToken)
    {
        BroadcastSession? session = null;
        SessionEventCapture? eventCapture = null;
        RecordingInactivityMonitor? inactivity = null;
        RecordingSessionLifecycle? lifecycle = null;
        LocalRecordingCapture? capture = null;
        LiveRoomSnapshot? pendingRoom = null;
        Streamer? target = null;
        var sessionPublished = false;
        var publicationPending = false;
        var retrySeconds = 2;
        var retryDelay = TimeSpan.Zero;
        var defaults = new ServerSettings();
        var monitorIntervalSeconds = defaults.MonitorIntervalSeconds;
        var timeoutMinutes = defaults.StreamDisconnectTimeoutMinutes;
        try
        {
            var continuation = await projections.GetContinuationAsync(id, cancellationToken);
            if (continuation is not null)
            {
                session = await context.Collection<BroadcastSession>("sessions").Find(value => value.Id == continuation.LogicalSessionId)
                    .FirstOrDefaultAsync(cancellationToken);
                sessionPublished = session is not null;
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var token = inactivity?.Token ?? cancellationToken;
                    await Task.Delay(retryDelay, token);
                    retryDelay = TimeSpan.Zero;
                    var settings = await settingsStore.GetAsync(token);
                    monitorIntervalSeconds = settings.MonitorIntervalSeconds;
                    timeoutMinutes = settings.StreamDisconnectTimeoutMinutes;
                    if (session is not null)
                    {
                        session.Lifecycle ??= new()
                        {
                            LastMediaReceivedAtUtc = session.LastMediaReceivedAtUtc,
                            TimeoutAnchorUtc = clock.GetUtcNow().UtcDateTime, LiveTime = session.LiveTime
                        };
                        lifecycle ??= new(session.Lifecycle, clock, settings.SessionGraceMinutes, timeoutMinutes);
                        inactivity ??= new(TimeSpan.FromMinutes(timeoutMinutes), clock,
                            lifecycle.State.LastMediaReceivedAtUtc ?? session.LastMediaReceivedAtUtc ?? lifecycle.State.TimeoutAnchorUtc, cancellationToken);
                        inactivity.Configure(TimeSpan.FromMinutes(timeoutMinutes));
                        if (lifecycle.Evaluate(settings.SessionGraceMinutes, timeoutMinutes) != RecordingSessionEndReason.None)
                        {
                            await StopEventCaptureAsync(eventCapture);
                            eventCapture = null;
                            await CloseSessionAsync(session, capture, lifecycle, cancellationToken);
                            session = null;
                            capture = null;
                            lifecycle = null;
                            sessionPublished = false;
                            publicationPending = false;
                            inactivity.Dispose();
                            inactivity = null;
                            pendingRoom = null;
                            continue;
                        }
                        token = inactivity.Token;
                    }
                    target = await context.Collection<Streamer>("streamers").Find(value => value.Id == id).FirstOrDefaultAsync(token);
                    if (target is null || !target.Enabled || !target.IsMonitored)
                    {
                        break;
                    }
                    var room = pendingRoom ?? await GetRoomWithLifecycleAsync(target, lifecycle, false, monitorIntervalSeconds, token);
                    pendingRoom = null;
                    if (session is not null && lifecycle is not null)
                    {
                        lifecycle.ObserveRoom(room.IsLive, GetLiveTime(room), clock.GetUtcNow().UtcDateTime);
                        session.Lifecycle = lifecycle.State;
                        await SaveSessionStateAsync(session, token);
                        if (lifecycle.Evaluate() != RecordingSessionEndReason.None)
                        {
                            await StopEventCaptureAsync(eventCapture);
                            eventCapture = null;
                            await CloseSessionAsync(session, capture, lifecycle, cancellationToken);
                            session = null;
                            capture = null;
                            lifecycle = null;
                            sessionPublished = false;
                            publicationPending = false;
                            inactivity?.Dispose();
                            inactivity = null;
                            continue;
                        }
                    }
                    await UpdateTargetAsync(target, room, room.IsLive && sessionPublished ? "Recording" : "Waiting", null, token);
                    if (!room.IsLive)
                    {
                        if (session is not null)
                        {
                            await UpdateSessionStateAsync(session, "Waiting", token);
                        }
                        retryDelay = TimeSpan.FromSeconds(monitorIntervalSeconds);
                        continue;
                    }
                    if (AvailableBytes(paths.RecordsDirectory) < settings.MinimumFreeBytes)
                    {
                        throw new InfrastructureException(MessageCode.RECORDING_DISK_SPACE_LOW, 507);
                    }
                    var source = await bilibili.ResolveStreamAsync(target.RoomId, target.Quality ?? settings.Quality, target.AccountId, token);
                    if (session is null)
                    {
                        session = PrepareSession(target, room, GetLiveTime(room), settings);
                        session.Lifecycle = new()
                        {
                            TimeoutAnchorUtc = clock.GetUtcNow().UtcDateTime, LiveTime = GetLiveTime(room)
                        };
                        lifecycle = new(session.Lifecycle, clock, settings.SessionGraceMinutes, timeoutMinutes);
                        lifecycle.ObserveRoom(true, GetLiveTime(room), clock.GetUtcNow().UtcDateTime);
                        session.Lifecycle = lifecycle.State;
                        sessionPublished = false;
                        publicationPending = true;
                        inactivity = new(TimeSpan.FromMinutes(timeoutMinutes), clock, session.Lifecycle.TimeoutAnchorUtc, cancellationToken);
                        token = inactivity.Token;
                    }
                    capture ??= await PrepareCaptureAsync(session, token);
                    var directory = MediaPaths.Resolve(paths.RecordsDirectory, capture.DirectoryPath);
                    var attemptId = Guid.NewGuid().ToString("N");
                    var activeSession = session;
                    var activeTarget = target;
                    var activeCapture = capture;
                    var activeLifecycle = lifecycle!;
                    var activity = inactivity!;
                    var hasActiveSession = sessionPublished;
                    var needsPublication = publicationPending;
                    var progress = new RecordingAttemptProgress(activity);
                    RecordingProgressSink? sink = null;
                    eventCapture ??= new(activeSession, activeTarget, directory,
                        (value, streamer, writer, stop) => CaptureEventsAsync(value, streamer, writer, activeLifecycle,
                            activeCapture.StartedAtUtc, stop), cancellationToken, activeTarget.RecordDanmu ?? settings.RecordDanmu);
                    try
                    {
                        if (hasActiveSession)
                        {
                            await RegisterCaptureAsync(activeSession, activeCapture, token);
                            var existing = await context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == activeSession.Id).ToListAsync(token);
                            sink = new(activeSession, existing, attemptId, directory, context, publisher, logger,
                                projections, activeCapture.Id, activeCapture.RelativePathPrefix, activeLifecycle, clock);
                            progress.Activate(sink);
                        }
                        async Task ActivateSessionAsync()
                        {
                            if (!hasActiveSession)
                            {
                                await RegisterCaptureAsync(activeSession, activeCapture, CancellationToken.None);
                                hasActiveSession = true;
                                sink = new(activeSession, [], attemptId, directory, context, publisher, logger,
                                    projections, activeCapture.Id, activeCapture.RelativePathPrefix, activeLifecycle, clock);
                                progress.Activate(sink);
                            }
                            if (needsPublication)
                            {
                                await sink!.FlushAsync();
                                await PublishSessionAsync(activeSession, activeTarget, room, CancellationToken.None);
                                needsPublication = false;
                            }
                            await UpdateSessionStateAsync(activeSession, "Recording", CancellationToken.None);
                            await UpdateTargetAsync(activeTarget, room, "Recording", null, CancellationToken.None);
                            await WriteTaskLogAsync(activeSession, "Capturing", "Information", "Valid media is being captured in the broadcast archive.", CancellationToken.None);
                        }
                        pendingRoom = await RecordUntilRoomChangesAsync(new RecordingRequest
                        {
                            SessionId = activeSession.Id, AttemptId = attemptId, RoomId = target.RoomId,
                            StreamUrl = source.Url, Headers = source.Headers.ToDictionary(), OutputDirectory = directory,
                            Title = room.Title, MaxSegmentBytes = target.SegmentBytes ?? settings.SegmentBytes,
                            StartedAtUtc = clock.GetUtcNow().UtcDateTime, SessionStartedAtUtc = activeSession.StartedAtUtc,
                            FileTimeZoneId = settings.TimeZone
                        }, progress, ActivateSessionAsync, target, activeLifecycle, room, monitorIntervalSeconds, token);
                    }
                    finally
                    {
                        sessionPublished = hasActiveSession;
                        publicationPending = needsPublication;
                        if (sink is not null)
                        {
                            await sink.DisposeAsync();
                        }
                    }
                    retrySeconds = 2;
                    if (pendingRoom is not null)
                    {
                        continue;
                    }
                    retryDelay = TimeSpan.FromSeconds(retrySeconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (inactivity is { IsExpired: true })
                {
                    await StopEventCaptureAsync(eventCapture);
                    eventCapture = null;
                    var error = new InfrastructureException(MessageCode.RECORDING_STREAM_TIMEOUT, arguments: [timeoutMinutes]);
                    if (target is not null)
                    {
                        SetRecordingError(target, session, error);
                        await TrySaveTargetAsync(target, cancellationToken);
                    }
                    if (session is not null && sessionPublished && lifecycle is not null)
                    {
                        lifecycle.Evaluate();
                        await WriteTaskLogAsync(session, "Capturing", "Information",
                            "The configured interval without valid media elapsed; the broadcast archive will close and room monitoring will continue. Interval (minutes): "
                            + timeoutMinutes.ToString(CultureInfo.InvariantCulture) + ".", cancellationToken);
                        await CloseSessionAsync(session, capture, lifecycle, cancellationToken);
                    }
                    else if (session is not null)
                    {
                        DiscardDraft(session);
                    }
                    session = null;
                    capture = null;
                    lifecycle = null;
                    sessionPublished = false;
                    publicationPending = false;
                    pendingRoom = null;
                    inactivity.Dispose();
                    inactivity = null;
                    retrySeconds = 2;
                    retryDelay = TimeSpan.FromSeconds(monitorIntervalSeconds);
                }
                catch (Exception error)
                {
                    if (session is not null && sessionPublished)
                    {
                        await WriteTaskLogAsync(session, "Capturing", "Warning",
                            "Recording will retry; committed media remains in the broadcast archive (" + error.GetType().Name + "). " + error.Message, cancellationToken);
                    }
                    if (target is not null)
                    {
                        SetRecordingError(target, session, error);
                        await TrySaveTargetAsync(target, cancellationToken);
                        logger.LogWarning("Recording will retry for room {RoomId}: {Message}", target.RoomId, error.Message);
                    }
                    retryDelay = TimeSpan.FromSeconds(retrySeconds);
                    retrySeconds = Math.Min(30, retrySeconds * 2);
                }
                finally
                {
                    if (session is not null && !sessionPublished)
                    {
                        await StopEventCaptureAsync(eventCapture);
                        eventCapture = null;
                        DiscardDraft(session);
                        session = null;
                        capture = null;
                        lifecycle = null;
                        inactivity?.Dispose();
                        inactivity = null;
                        publicationPending = false;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            inactivity?.Dispose();
            await StopEventCaptureAsync(eventCapture);
            if (session is not null && sessionPublished && lifecycle is not null)
            {
                try
                {
                    session.Lifecycle = lifecycle.State;
                    await SealCaptureAsync(session, capture, CancellationToken.None);
                }
                catch (Exception error)
                {
                    logger.LogWarning("The session pause state will be saved when the database is available again: {Message}", error.Message);
                }
            }
        }
    }

    private static void SetRecordingError(Streamer target, BroadcastSession? session, Exception error)
    {
        target.State = "Retrying";
        target.Error = error.Message;
        target.ErrorCode = (error as InfrastructureException)?.Code ?? nameof(MessageCode.RECORDING_CAPTURE_FAILED);
        target.ErrorArguments = error is InfrastructureException { Code: not null } businessError ? [.. businessError.Arguments] : [error.Message];
        if (session is not null)
        {
            session.Error = error.Message;
            session.ErrorCode = target.ErrorCode;
            session.ErrorArguments = [.. target.ErrorArguments];
        }
    }

    private void DiscardDraft(BroadcastSession session)
    {
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException error)
        {
            logger.LogWarning("Cleanup is pending for recording directory {Directory}, which contains no valid media: {Message}", directory, error.Message);
        }
    }

    private async Task<LiveRoomSnapshot?> RecordUntilRoomChangesAsync(RecordingRequest request, RecordingAttemptProgress progress, Func<Task> activateSession, Streamer target, RecordingSessionLifecycle lifecycle, LiveRoomSnapshot initialRoom, int monitorIntervalSeconds, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var recording = recorder.RecordAsync(request, progress, lifetime.Token);
        var roomChange = WaitForRoomChangeAsync(request.SessionId, target, lifecycle, initialRoom, monitorIntervalSeconds, progress, lifetime.Token);
        ExceptionDispatchInfo? failure = null;
        var activationStarted = false;
        async Task ActivateAsync()
        {
            if (!activationStarted && progress.HasMedia)
            {
                activationStarted = true;
                await activateSession();
            }
        }
        try
        {
            await Task.WhenAny(recording, roomChange, progress.MediaReceived);
            await ActivateAsync();
            await Task.WhenAny(recording, roomChange);
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }
        try
        {
            await lifetime.CancelAsync();
        }
        catch (Exception error)
        {
            failure ??= ExceptionDispatchInfo.Capture(error);
        }
        try
        {
            await Task.WhenAll(recording, roomChange);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure ??= ExceptionDispatchInfo.Capture(error);
        }
        try
        {
            await ActivateAsync();
        }
        catch (Exception error)
        {
            failure ??= ExceptionDispatchInfo.Capture(error);
        }
        failure?.Throw();
        return roomChange.IsCompletedSuccessfully ? await roomChange : null;
    }

    private async Task<LiveRoomSnapshot> WaitForRoomChangeAsync(string sessionId, Streamer target, RecordingSessionLifecycle lifecycle, LiveRoomSnapshot initialRoom, int monitorIntervalSeconds, RecordingAttemptProgress progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (lifecycle.State.LastObservedIsLive == false || lifecycle.Evaluate() != RecordingSessionEndReason.None)
            {
                return initialRoom with { IsLive = false };
            }
            await Task.Delay(TimeSpan.FromSeconds(monitorIntervalSeconds), cancellationToken);
            try
            {
                var room = await GetRoomWithLifecycleAsync(target, lifecycle, true, monitorIntervalSeconds, cancellationToken);
                lifecycle.ObserveRoom(room.IsLive, GetLiveTime(room), clock.GetUtcNow().UtcDateTime);
                await projections.SynchronizeLifecycleAsync(sessionId, lifecycle.State, cancellationToken);
                if (!room.IsLive || lifecycle.Evaluate() != RecordingSessionEndReason.None)
                {
                    return room;
                }
                await UpdateTargetAsync(target, room, progress.HasMedia ? "Recording" : target.State, null, cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Room {RoomId} status lookup is deferred. The current recording will continue: {Message}", target.RoomId, error.Message);
            }
        }
    }

    private async Task<LiveRoomSnapshot> GetRoomWithLifecycleAsync(Streamer target, RecordingSessionLifecycle? lifecycle,
        bool stopOnPreparing, int monitorIntervalSeconds, CancellationToken cancellationToken)
    {
        using var lookupStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lookup = bilibili.GetRoomAsync(target.RoomId, target.AccountId, lookupStop.Token);
        while (!lookup.IsCompleted)
        {
            await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(monitorIntervalSeconds), lookupStop.Token));
            cancellationToken.ThrowIfCancellationRequested();
            if (lifecycle is not null && (lifecycle.Evaluate() != RecordingSessionEndReason.None
                || stopOnPreparing && lifecycle.State.LastObservedIsLive == false))
            {
                await lookupStop.CancelAsync();
                try
                {
                    await lookup;
                }
                catch (OperationCanceledException) when (lookupStop.IsCancellationRequested)
                {
                }
                return new(target.RoomId, target.Uid, target.Name, target.AvatarUrl, "", "", false, null);
            }
        }
        return await lookup;
    }

    private static long GetLiveTime(LiveRoomSnapshot room)
    {
        return room.LiveStartedAtUtc is { } liveAt ? new DateTimeOffset(liveAt).ToUnixTimeSeconds() : 0;
    }

    private async Task StopEventCaptureAsync(SessionEventCapture? capture)
    {
        if (capture is null)
        {
            return;
        }
        try
        {
            await capture.DisposeAsync();
        }
        catch (Exception error)
        {
            logger.LogError(error, "Failed to finalize the chat archive");
        }
    }

    private async Task CaptureEventsAsync(BroadcastSession session, Streamer target, SessionEventWriter? writer,
        RecordingSessionLifecycle lifecycle, DateTime captureStartedAtUtc, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await bilibili.SubscribeEventsAsync(target.RoomId, target.AccountId, async (item, token) =>
                {
                    item.ReceivedAtUtc = item.ReceivedAtUtc == default ? clock.GetUtcNow().UtcDateTime : item.ReceivedAtUtc;
                    if (item.Command is "PREPARING" or "LIVE")
                    {
                        lifecycle.ObserveSignal(item.Command, item.ReceivedAtUtc);
                        session.Lifecycle = lifecycle.State;
                        await projections.SynchronizeLifecycleAsync(session.Id, session.Lifecycle, token);
                    }
                    var time = item.SourceTimeUtc ?? item.ReceivedAtUtc;
                    item.OffsetMs = Math.Max(0, (time - captureStartedAtUtc).TotalMilliseconds);
                    if (writer is not null)
                    {
                        await writer.WriteAsync(item, token);
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                session.Error = "弹幕采集缺失：" + error.Message;
                session.ErrorCode = (error as InfrastructureException)?.Code ?? MessageCode.RECORDING_EVENT_CAPTURE_GAP.ToString();
                session.ErrorArguments = error is InfrastructureException { Code: not null } businessError ? businessError.Arguments.ToList() : [error.Message];
                if (writer is not null)
                {
                    await writer.WriteAsync(new RecordedEventEnvelope
                    {
                        Kind = "Gap", ReceivedAtUtc = clock.GetUtcNow().UtcDateTime,
                        OffsetMs = Math.Max(0, (clock.GetUtcNow().UtcDateTime - captureStartedAtUtc).TotalMilliseconds),
                        Text = LocalizedText.Format(MessageCode.RECORDING_EVENT_CONNECTION_LOST.ToString(), "", culture: CultureInfo.InvariantCulture),
                        TextCode = MessageCode.RECORDING_EVENT_CONNECTION_LOST.ToString()
                    }, cancellationToken);
                }
                logger.LogWarning("The chat connection for room {RoomId} will reconnect: {Message}", target.RoomId, error.Message);
                await WriteTaskLogAsync(session, "Capturing", "Warning",
                    "The event connection will reconnect; committed events remain in the broadcast archive (" + error.GetType().Name + "). " + error.Message, cancellationToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private BroadcastSession PrepareSession(Streamer target, LiveRoomSnapshot room, long liveTime, ServerSettings settings)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var session = new BroadcastSession
        {
            StreamerId = target.Id,
            RoomId = room.RoomId,
            Title = room.Title,
            CoverUrl = room.CoverUrl,
            LiveTime = liveTime,
            StartedAtUtc = now,
            LastMediaAtUtc = now,
            HasDanmu = target.RecordDanmu ?? settings.RecordDanmu,
            AutoTranscodeEligible = true
        };
        var name = TimeZoneInfo.ConvertTimeFromUtc(now, zone).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var streamerDirectory = string.IsNullOrEmpty(target.DirectoryName) ? MediaPaths.SafeName(room.Name) + "_" + room.Uid : target.DirectoryName;
        target.DirectoryName = streamerDirectory;
        session.DirectoryPath = Path.Combine(streamerDirectory, name + "_" + MediaPaths.SafeName(room.Title) + "_" + session.Id[..8]);
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        Directory.CreateDirectory(directory);
        return session;
    }

    private async Task PublishSessionAsync(BroadcastSession session, Streamer target, LiveRoomSnapshot room, CancellationToken cancellationToken)
    {
        await JsonFiles.WriteAsync(Path.Combine(paths.RecordsDirectory, target.DirectoryName, "主播信息.json"), room, cancellationToken);
        await TrySaveTargetAsync(target, cancellationToken);
        await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
        await WriteTaskLogAsync(session, "Capturing", "Information", "The local broadcast archive was published after receiving valid media.",
            cancellationToken, "recording-start:" + session.Id, session.StartedAtUtc);
    }

    private async Task CloseSessionAsync(BroadcastSession session, LocalRecordingCapture? capture,
        RecordingSessionLifecycle lifecycle, CancellationToken cancellationToken)
    {
        session.Lifecycle = lifecycle.State;
        await SaveSessionStateAsync(session, cancellationToken);
        await SealCaptureAsync(session, capture, cancellationToken);
        session.EndedAtUtc = session.LastMediaAtUtc;
        session.State = "Completed";
        await projections.SynchronizeLifecycleAsync(session.Id, session.Lifecycle, cancellationToken);
        await WriteTaskLogAsync(session, "Capturing", "Information", "The local broadcast archive is closed; committed media has been preserved.",
            cancellationToken, "recording-close:" + session.Id + ":" + session.EndedAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond, session.EndedAtUtc);
        await WriteTaskLogAsync(session, "Uploading", "Information", "The broadcast archive is stored on the main server; no worker upload is required.",
            cancellationToken, "recording-local-archive:" + session.Id + ":" + session.EndedAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond, session.EndedAtUtc);
    }

    private async Task<LocalRecordingCapture> PrepareCaptureAsync(BroadcastSession session, CancellationToken cancellationToken)
    {
        var captures = await projections.GetBindingsAsync(session.Id, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var prefix = captures.Count == 0 ? "" : Path.Combine(".captures", id);
        var relative = Path.Combine(session.DirectoryPath, prefix);
        Directory.CreateDirectory(MediaPaths.Resolve(paths.RecordsDirectory, relative));
        return new(id, relative, prefix, captures.Count == 0 ? session.StartedAtUtc : clock.GetUtcNow().UtcDateTime);
    }

    private Task<RecordingCaptureBinding> RegisterCaptureAsync(BroadcastSession session, LocalRecordingCapture capture, CancellationToken cancellationToken) =>
        projections.RegisterLocalCaptureAsync(session, capture.Id, capture.DirectoryPath, capture.StartedAtUtc, null, 0, cancellationToken);

    private async Task SealCaptureAsync(BroadcastSession session, LocalRecordingCapture? capture, CancellationToken cancellationToken)
    {
        session.State = "Waiting";
        session.EndedAtUtc = null;
        if (capture is not null)
        {
            var segments = await context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == session.Id).ToListAsync(cancellationToken);
            await projections.PublishLocalAsync(session, segments, capture.Id, true, cancellationToken);
        }
        else if (session.Lifecycle is not null)
        {
            await projections.SynchronizeLifecycleAsync(session.Id, session.Lifecycle, cancellationToken);
        }
    }

    private Task WriteTaskLogAsync(BroadcastSession session, string stage, string level, string message, CancellationToken cancellationToken,
        string? eventId = null, DateTime? timestampUtc = null)
    {
        return taskLogs?.WriteRecordingAsync("local", session.Id, stage, level, message, timestampUtc ?? clock.GetUtcNow().UtcDateTime,
            eventId, cancellationToken: cancellationToken) ?? Task.CompletedTask;
    }

    private async Task UpdateSessionStateAsync(BroadcastSession session, string state, CancellationToken cancellationToken)
    {
        if (session.State == state)
        {
            return;
        }
        var previousState = session.State;
        session.State = state;
        try
        {
            await SaveSessionStateAsync(session, cancellationToken);
        }
        catch
        {
            session.State = previousState;
            throw;
        }
    }

    private async Task SaveSessionStateAsync(BroadcastSession session, CancellationToken cancellationToken)
    {
        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == session.Id).ToListAsync(cancellationToken);
        var update = Builders<BroadcastSession>.Update.Set(x => x.Error, session.Error).Set(x => x.ErrorCode, session.ErrorCode)
            .Set(x => x.ErrorArguments, session.ErrorArguments);
        if (session.Lifecycle is null)
        {
            update = update.Set(x => x.EndedAtUtc, session.EndedAtUtc).Set(x => x.State, session.State)
                .Set(x => x.LastMediaReceivedAtUtc, session.LastMediaReceivedAtUtc);
        }
        var current = await context.Collection<BroadcastSession>("sessions").FindOneAndUpdateAsync(x => x.Id == session.Id,
            update,
            new FindOneAndUpdateOptions<BroadcastSession> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        if (current is null)
        {
            await context.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == session.Id, session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
        if (session.Lifecycle is not null)
        {
            await projections.SynchronizeLifecycleAsync(session.Id, session.Lifecycle, cancellationToken);
        }
        else
        {
            await SessionManifestFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath), "场次.json"), new SessionManifest { Session = current ?? session, Segments = segments }, cancellationToken);
        }
        await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
    }

    private async Task UpdateTargetAsync(Streamer target, LiveRoomSnapshot room, string state, string? error, CancellationToken cancellationToken)
    {
        target.RoomId = room.RoomId;
        target.Uid = room.Uid;
        target.Name = room.Name;
        target.AvatarUrl = room.AvatarUrl;
        target.IsLive = room.IsLive;
        target.State = state;
        target.Error = error;
        target.ErrorCode = null;
        target.ErrorArguments = [];
        target.LastCheckedAtUtc = clock.GetUtcNow().UtcDateTime;
        await TrySaveTargetAsync(target, cancellationToken);
    }

    private async Task TrySaveTargetAsync(Streamer target, CancellationToken cancellationToken)
    {
        try
        {
            await context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == target.Id && x.AvatarUrl != target.AvatarUrl,
                Builders<Streamer>.Update.Set(x => x.AvatarUrl, target.AvatarUrl).Set(x => x.AvatarRelativePath, null), cancellationToken: cancellationToken);
            await context.Collection<Streamer>("streamers").UpdateOneAsync(x => x.Id == target.Id,
                Builders<Streamer>.Update.Set(x => x.RoomId, target.RoomId).Set(x => x.Uid, target.Uid)
                    .Set(x => x.Name, target.Name).Set(x => x.DirectoryName, target.DirectoryName)
                    .Set(x => x.IsLive, target.IsLive).Set(x => x.State, target.State).Set(x => x.Error, target.Error)
                    .Set(x => x.ErrorCode, target.ErrorCode).Set(x => x.ErrorArguments, target.ErrorArguments)
                    .Set(x => x.LastCheckedAtUtc, target.LastCheckedAtUtc), cancellationToken: cancellationToken);
            await publisher.PublishAsync("streamer", target.Id, cancellationToken: cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Room management state will be saved when the database is available again: {Message}", error.Message);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RecordsDirectory);
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        foreach (var path in Directory.EnumerateFiles(paths.RecordsDirectory, "场次.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (!SessionTrashService.CanReconcile(paths.RecordsDirectory, path))
            {
                continue;
            }
            try
            {
                await using var file = File.OpenRead(path);
                var manifest = await JsonSerializer.DeserializeAsync<SessionManifest>(file, JsonFiles.Options, cancellationToken);
                if (manifest is null)
                {
                    continue;
                }
                if (manifest.Captures.Count == 0 && manifest.Session.RecordingAssignmentId is null && manifest.Segments.Count > 0)
                {
                    manifest.Captures.Add(new()
                    {
                        Id = manifest.Session.Id, LogicalSessionId = manifest.Session.Id, DirectoryPath = manifest.Session.DirectoryPath,
                        StartedAtUtc = manifest.Session.StartedAtUtc, IsLocal = true, IsClosed = true,
                        HasDanmu = manifest.Session.HasDanmu, LastMediaReceivedAtUtc = manifest.Session.LastMediaReceivedAtUtc
                    });
                }
                foreach (var capture in manifest.Captures)
                {
                    if (capture.IsLocal)
                    {
                        capture.IsClosed = true;
                    }
                    await context.Collection<RecordingCaptureBinding>("recordingCaptureBindings").ReplaceOneAsync(value => value.Id == capture.Id,
                        capture, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                }
                await RestoreLocalActivityAsync(manifest, cancellationToken);
                await context.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == manifest.Session.Id, manifest.Session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                foreach (var segment in manifest.Segments)
                {
                    segment.AssetId = TranscodeAssetRegistry.AssetId(manifest.Session, segment);
                    var relative = segment.RootRelativePath ?? Path.Combine(manifest.Session.DirectoryPath, segment.RelativePath);
                    if (manifest.Captures.Any(value => value.IsLocal && relative.StartsWith(value.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    {
                        segment.IsClosed = true;
                    }
                    await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id, segment, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                }
            }
            catch (JsonException error)
            {
                logger.LogWarning("Could not read archive manifest {Path}: {Message}", path, error.Message);
            }
        }
        await segmentTrash.ReconcileUnderLibraryLockAsync(cancellationToken);
        await grouping.ReconcileUnderLibraryLockAsync(cancellationToken);
    }

    private async Task RestoreLocalActivityAsync(SessionManifest manifest, CancellationToken cancellationToken)
    {
        var received = manifest.Session.LastMediaReceivedAtUtc;
        foreach (var segment in manifest.Segments.Where(value => value.DurationMs > 0))
        {
            var relative = segment.RootIndexRelativePath ?? Path.Combine(manifest.Session.DirectoryPath, segment.IndexRelativePath);
            if (!manifest.Captures.Any(value => value.IsLocal && relative.StartsWith(value.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                continue;
            }
            var index = MediaPaths.Resolve(paths.RecordsDirectory, relative);
            if (!File.Exists(index))
            {
                continue;
            }
            using var reader = new StreamReader(index);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("lastMediaReceivedAtUtc", out var stamp)
                        && stamp.ValueKind == JsonValueKind.String && stamp.TryGetDateTime(out var value)
                        && (received is null || value > received))
                    {
                        received = value;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        if (manifest.Session.Lifecycle is { Revision: > 0, LastMediaReceivedAtUtc: not null } lifecycle)
        {
            manifest.Session.LastMediaReceivedAtUtc = lifecycle.LastMediaReceivedAtUtc;
        }
        else
        {
            manifest.Session.LastMediaReceivedAtUtc = received;
            if (manifest.Session.Lifecycle is not null)
            {
                manifest.Session.Lifecycle.LastMediaReceivedAtUtc = received;
            }
        }
    }

    private static long AvailableBytes(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return DriveInfo.GetDrives().Where(x => fullPath.StartsWith(x.RootDirectory.FullName, StringComparison.Ordinal)).OrderByDescending(x => x.RootDirectory.FullName.Length).First().AvailableFreeSpace;
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "A recording subtask ended and will be rescheduled by the supervisor");
        }
    }
}
