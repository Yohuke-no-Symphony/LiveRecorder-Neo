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

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>并行维护启用的直播间，数据库和派生任务不进入采集热路径。</summary>
public sealed class RecordingSupervisor(IMongoContext context, IBootstrapStore bootstrap, ISettingsStore settingsStore, IBilibiliService bilibili, IMediaRecorder recorder, IStatusPublisher publisher, ILogger<RecordingSupervisor> logger, SessionGroupingService grouping, SessionSegmentTrashService segmentTrash, IAppPaths paths, TimeProvider? timeProvider = null, RecordingOwnershipService? ownership = null, RecordingHandoffService? handoff = null, TaskEventLogStore? taskLogs = null) : BackgroundService
{
    private readonly Dictionary<string, RecordingWorkerHandle> workers = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly string localOwnerId = "local-" + TranscodeAssetRegistry.Hash(bootstrap.InstallationToken + ":" + Environment.MachineName);
    private readonly RecordingHandoffService handoffs = handoff ?? new(context, paths, publisher);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconciled = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (bootstrap.IsInstalled)
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
                            var open = await context.Collection<BroadcastSession>("sessions").Find(x => x.StreamerId == item.Key && x.EndedAtUtc == null && !x.IsHidden && x.RecordingAssignmentId == null).FirstOrDefaultAsync(stoppingToken);
                            if (open is not null)
                            {
                                await CloseSessionAsync(open, DateTime.UtcNow, stoppingToken);
                            }
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
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("录制目标同步暂缓：{Message}", error.Message);
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
        LiveRoomSnapshot? pendingRoom = null;
        Streamer? target = null;
        DateTime? offlineSince = null;
        var sessionPublished = false;
        var publicationPending = false;
        var retrySeconds = 2;
        var retryDelay = TimeSpan.Zero;
        var defaults = new ServerSettings();
        var monitorIntervalSeconds = defaults.MonitorIntervalSeconds;
        var timeoutMinutes = defaults.StreamDisconnectTimeoutMinutes;
        try
        {
            session = await context.Collection<BroadcastSession>("sessions").Find(x => x.StreamerId == id && x.EndedAtUtc == null && !x.IsTrashed && x.RecordingAssignmentId == null).SortByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            sessionPublished = session is not null;
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
                        inactivity ??= new(TimeSpan.FromMinutes(timeoutMinutes), clock, cancellationToken);
                        inactivity.Configure(TimeSpan.FromMinutes(timeoutMinutes));
                        token = inactivity.Token;
                    }
                    target = await context.Collection<Streamer>("streamers").Find(x => x.Id == id).FirstOrDefaultAsync(token);
                    if (target is null || !target.Enabled || !target.IsMonitored)
                    {
                        break;
                    }
                    var room = pendingRoom ?? await bilibili.GetRoomAsync(target.RoomId, target.AccountId, token);
                    pendingRoom = null;
                    await UpdateTargetAsync(target, room, room.IsLive && sessionPublished ? "Recording" : "Waiting", null, token);
                    if (!room.IsLive)
                    {
                        offlineSince ??= DateTime.UtcNow;
                        await StopEventCaptureAsync(eventCapture);
                        eventCapture = null;
                        if (session is not null)
                        {
                            if (DateTime.UtcNow - offlineSince >= TimeSpan.FromMinutes(settings.SessionGraceMinutes))
                            {
                                await CloseSessionAsync(session, offlineSince.Value, token);
                                session = null;
                                sessionPublished = false;
                                inactivity?.Dispose();
                                inactivity = null;
                            }
                            else
                            {
                                await UpdateSessionStateAsync(session, "Waiting", token);
                            }
                        }
                        retryDelay = TimeSpan.FromSeconds(monitorIntervalSeconds);
                        continue;
                    }
                    var liveTime = GetLiveTime(room);
                    var graceExpired = offlineSince is { } offlineAt && DateTime.UtcNow - offlineAt >= TimeSpan.FromMinutes(settings.SessionGraceMinutes);
                    if (session is not null && (graceExpired || session.LiveTime > 0 && liveTime > 0 && session.LiveTime != liveTime))
                    {
                        await StopEventCaptureAsync(eventCapture);
                        eventCapture = null;
                        await CloseSessionAsync(session, offlineSince ?? session.LastMediaAtUtc, token);
                        session = null;
                        sessionPublished = false;
                        inactivity?.Dispose();
                        inactivity = null;
                        token = cancellationToken;
                    }
                    offlineSince = null;
                    if (AvailableBytes(paths.RecordsDirectory) < settings.MinimumFreeBytes)
                    {
                        throw new InfrastructureException(MessageCode.RECORDING_DISK_SPACE_LOW, 507);
                    }
                    var source = await bilibili.ResolveStreamAsync(target.RoomId, target.Quality ?? settings.Quality, target.AccountId, token);
                    if (session is null)
                    {
                        session = PrepareSession(target, room, liveTime, settings);
                        sessionPublished = false;
                        publicationPending = true;
                        inactivity = new(TimeSpan.FromMinutes(timeoutMinutes), clock, cancellationToken);
                        token = inactivity.Token;
                    }
                    var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
                    var attemptId = Guid.NewGuid().ToString("N");
                    var activeSession = session;
                    var activeTarget = target;
                    var activity = inactivity!;
                    var activeEvents = eventCapture;
                    var hasActiveSession = sessionPublished;
                    var needsPublication = publicationPending;
                    var progress = new RecordingAttemptProgress(activity);
                    RecordingProgressSink? sink = null;
                    try
                    {
                        if (hasActiveSession)
                        {
                            var sessionId = activeSession.Id;
                            var existing = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == sessionId).ToListAsync(token);
                            sink = new(activeSession, existing, attemptId, directory, context, publisher, logger);
                            progress.Activate(sink);
                        }
                        async Task ActivateSessionAsync()
                        {
                            if (!hasActiveSession)
                            {
                                hasActiveSession = true;
                                sink = new(activeSession, [], attemptId, directory, context, publisher, logger);
                                progress.Activate(sink);
                            }
                            if (needsPublication)
                            {
                                await PublishSessionAsync(activeSession, activeTarget, room, cancellationToken);
                                needsPublication = false;
                            }
                            await UpdateSessionStateAsync(activeSession, "Recording", cancellationToken);
                            await UpdateTargetAsync(activeTarget, room, "Recording", null, cancellationToken);
                            await WriteTaskLogAsync(activeSession, "Capturing", "Information", "Valid media is being captured in the broadcast archive.", cancellationToken);
                            if ((activeTarget.RecordDanmu ?? settings.RecordDanmu) && activeEvents is null)
                            {
                                activeEvents = new(activeSession, activeTarget, directory, CaptureEventsAsync, activity.Token);
                            }
                        }
                        pendingRoom = await RecordUntilRoomChangesAsync(new RecordingRequest
                        {
                            SessionId = session.Id,
                            AttemptId = attemptId,
                            RoomId = target.RoomId,
                            StreamUrl = source.Url,
                            Headers = source.Headers.ToDictionary(),
                            OutputDirectory = directory,
                            Title = room.Title,
                            MaxSegmentBytes = target.SegmentBytes ?? settings.SegmentBytes,
                            StartedAtUtc = DateTime.UtcNow,
                            SessionStartedAtUtc = session.StartedAtUtc,
                            FileTimeZoneId = settings.TimeZone
                        }, progress, ActivateSessionAsync, target, session.LiveTime, monitorIntervalSeconds, token);
                    }
                    finally
                    {
                        sessionPublished = hasActiveSession;
                        publicationPending = needsPublication;
                        eventCapture = activeEvents;
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
                    if (session is not null && sessionPublished)
                    {
                        await WriteTaskLogAsync(session, "Capturing", "Information",
                            "The configured interval without valid media elapsed; the broadcast archive will close and room monitoring will continue. Interval (minutes): "
                            + timeoutMinutes.ToString(CultureInfo.InvariantCulture) + ".", cancellationToken);
                        await CloseSessionAsync(session, session.LastMediaAtUtc, cancellationToken);
                    }
                    else if (session is not null)
                    {
                        DiscardDraft(session);
                    }
                    logger.LogInformation("直播间 {RoomId} 连续 {Minutes} 分钟无有效媒体，结束当前场次并继续监控", target?.RoomId, timeoutMinutes);
                    session = null;
                    sessionPublished = false;
                    publicationPending = false;
                    pendingRoom = null;
                    offlineSince = null;
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
                        logger.LogWarning("直播间 {RoomId} 等待重试：{Message}", target.RoomId, error.Message);
                    }
                    else
                    {
                        logger.LogWarning("录制目标 {TargetId} 等待重试：{Message}", id, error.Message);
                    }
                    retryDelay = TimeSpan.FromSeconds(retrySeconds);
                    retrySeconds = Math.Min(30, retrySeconds * 2);
                }
                finally
                {
                    if (session is not null && !sessionPublished)
                    {
                        DiscardDraft(session);
                        session = null;
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
            if (session is not null && sessionPublished)
            {
                try
                {
                    target = await context.Collection<Streamer>("streamers").Find(x => x.Id == id).FirstOrDefaultAsync(CancellationToken.None);
                    if (target is null || !target.Enabled || !target.IsMonitored)
                    {
                        await CloseSessionAsync(session, DateTime.UtcNow, CancellationToken.None);
                    }
                }
                catch (Exception error)
                {
                    logger.LogWarning("场次停止状态等待数据库恢复：{Message}", error.Message);
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
            logger.LogWarning("未产生有效媒体的录制目录 {Directory} 等待清理：{Message}", directory, error.Message);
        }
    }

    private async Task<LiveRoomSnapshot?> RecordUntilRoomChangesAsync(RecordingRequest request, RecordingAttemptProgress progress, Func<Task> activateSession, Streamer target, long liveTime, int monitorIntervalSeconds, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var recording = recorder.RecordAsync(request, progress, lifetime.Token);
        var roomChange = WaitForRoomChangeAsync(target, liveTime, monitorIntervalSeconds, progress, lifetime.Token);
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

    private async Task<LiveRoomSnapshot> WaitForRoomChangeAsync(Streamer target, long liveTime, int monitorIntervalSeconds, RecordingAttemptProgress progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(monitorIntervalSeconds), cancellationToken);
            try
            {
                var room = await bilibili.GetRoomAsync(target.RoomId, target.AccountId, cancellationToken);
                var currentLiveTime = GetLiveTime(room);
                if (!room.IsLive || liveTime > 0 && currentLiveTime > 0 && liveTime != currentLiveTime)
                {
                    return room;
                }
                await UpdateTargetAsync(target, room, progress.HasMedia ? "Recording" : target.State, null, cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("直播间 {RoomId} 状态查询暂缓，继续当前录制：{Message}", target.RoomId, error.Message);
            }
        }
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
            logger.LogError(error, "弹幕归档结束时报告失败");
        }
    }

    private async Task CaptureEventsAsync(BroadcastSession session, Streamer target, SessionEventWriter writer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await bilibili.SubscribeEventsAsync(target.RoomId, target.AccountId, async (item, token) =>
                {
                    item.ReceivedAtUtc = item.ReceivedAtUtc == default ? DateTime.UtcNow : item.ReceivedAtUtc;
                    var time = item.SourceTimeUtc ?? item.ReceivedAtUtc;
                    item.OffsetMs = Math.Max(0, (time - session.StartedAtUtc).TotalMilliseconds);
                    await writer.WriteAsync(item, token);
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
                await writer.WriteAsync(new RecordedEventEnvelope
                {
                    Kind = "Gap",
                    ReceivedAtUtc = DateTime.UtcNow,
                    OffsetMs = Math.Max(0, (DateTime.UtcNow - session.StartedAtUtc).TotalMilliseconds),
                    Text = LocalizedText.Format(MessageCode.RECORDING_EVENT_CONNECTION_LOST.ToString(), "", culture: CultureInfo.InvariantCulture),
                    TextCode = MessageCode.RECORDING_EVENT_CONNECTION_LOST.ToString()
                }, cancellationToken);
                logger.LogWarning("直播间 {RoomId} 弹幕将重连：{Message}", target.RoomId, error.Message);
                await WriteTaskLogAsync(session, "Capturing", "Warning",
                    "The event connection will reconnect; committed events remain in the broadcast archive (" + error.GetType().Name + "). " + error.Message, cancellationToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private BroadcastSession PrepareSession(Streamer target, LiveRoomSnapshot room, long liveTime, ServerSettings settings)
    {
        var now = DateTime.UtcNow;
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
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        await SessionManifestFiles.WriteAsync(Path.Combine(directory, "场次.json"), new SessionManifest { Session = session }, cancellationToken);
        await JsonFiles.WriteAsync(Path.Combine(paths.RecordsDirectory, target.DirectoryName, "主播信息.json"), room, cancellationToken);
        await context.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == session.Id, session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        await TrySaveTargetAsync(target, cancellationToken);
        await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
        await WriteTaskLogAsync(session, "Capturing", "Information", "The local broadcast archive was published after receiving valid media.",
            cancellationToken, "recording-start:" + session.Id, session.StartedAtUtc);
    }

    private async Task CloseSessionAsync(BroadcastSession session, DateTime end, CancellationToken cancellationToken)
    {
        session.EndedAtUtc = end < session.LastMediaAtUtc ? session.LastMediaAtUtc : end;
        session.State = "Completed";
        await SaveSessionStateAsync(session, cancellationToken);
        await WriteTaskLogAsync(session, "Capturing", "Information", "The local broadcast archive is closed; committed media has been preserved.",
            cancellationToken, "recording-close:" + session.Id + ":" + session.EndedAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond, session.EndedAtUtc);
        await WriteTaskLogAsync(session, "Uploading", "Information", "The broadcast archive is stored on the main server; no worker upload is required.",
            cancellationToken, "recording-local-archive:" + session.Id + ":" + session.EndedAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond, session.EndedAtUtc);
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
        var current = await context.Collection<BroadcastSession>("sessions").FindOneAndUpdateAsync(x => x.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.EndedAtUtc, session.EndedAtUtc).Set(x => x.State, session.State)
                .Set(x => x.Error, session.Error).Set(x => x.ErrorCode, session.ErrorCode).Set(x => x.ErrorArguments, session.ErrorArguments),
            new FindOneAndUpdateOptions<BroadcastSession> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        if (current is null)
        {
            await context.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == session.Id, session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
        await SessionManifestFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath), "场次.json"), new SessionManifest { Session = current ?? session, Segments = segments }, cancellationToken);
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
        target.LastCheckedAtUtc = DateTime.UtcNow;
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
            logger.LogWarning("直播间管理状态等待数据库恢复：{Message}", error.Message);
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
                await context.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == manifest.Session.Id, manifest.Session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                foreach (var segment in manifest.Segments)
                {
                    segment.AssetId = TranscodeAssetRegistry.AssetId(manifest.Session, segment);
                    if (manifest.Session.RecordingAssignmentId is null)
                    {
                        segment.IsClosed = true;
                    }
                    await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id, segment, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                }
            }
            catch (JsonException error)
            {
                logger.LogWarning("档案清单 {Path} 无法读取：{Message}", path, error.Message);
            }
        }
        await segmentTrash.ReconcileUnderLibraryLockAsync(cancellationToken);
        await grouping.ReconcileUnderLibraryLockAsync(cancellationToken);
        var unfinished = await context.Collection<BroadcastSession>("sessions").Find(x => x.EndedAtUtc == null && !x.IsHidden && x.RecordingAssignmentId == null).ToListAsync(cancellationToken);
        foreach (var session in unfinished)
        {
            var target = await context.Collection<Streamer>("streamers").Find(x => x.Id == session.StreamerId).FirstOrDefaultAsync(cancellationToken);
            if (target is null || !target.Enabled || !target.IsMonitored)
            {
                await CloseSessionAsync(session, session.LastMediaAtUtc, cancellationToken);
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
            logger.LogError(error, "录制子任务结束，将由协调器重新调度");
        }
    }
}
