using System.Globalization;
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

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>并行维护启用的直播间，数据库和派生任务不进入采集热路径。</summary>
public sealed class RecordingSupervisor(IMongoContext context, IBootstrapStore bootstrap, ISettingsStore settingsStore, IBilibiliService bilibili, IMediaRecorder recorder, IStatusPublisher publisher, ILogger<RecordingSupervisor> logger, SessionGroupingService grouping, IAppPaths paths) : BackgroundService
{
    private readonly Dictionary<string, RecordingWorkerHandle> workers = new();

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
                    foreach (var item in workers.Where(x => !ids.Contains(x.Key) || x.Value.Task.IsCompleted).ToList())
                    {
                        await item.Value.Cancellation.CancelAsync();
                        await ObserveAsync(item.Value.Task);
                        if (!ids.Contains(item.Key))
                        {
                            var open = await context.Collection<BroadcastSession>("sessions").Find(x => x.StreamerId == item.Key && x.EndedAtUtc == null && !x.IsHidden).FirstOrDefaultAsync(stoppingToken);
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
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        workers[target.Id] = new RecordingWorkerHandle(cancellation, TrackAsync(target.Id, cancellation.Token));
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

    private async Task TrackAsync(string id, CancellationToken cancellationToken)
    {
        BroadcastSession? session = null;
        SessionEventCapture? eventCapture = null;
        DateTime? offlineSince = null;
        var retrySeconds = 2;
        try
        {
            session = await context.Collection<BroadcastSession>("sessions").Find(x => x.StreamerId == id && x.EndedAtUtc == null && !x.IsTrashed).SortByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = await settingsStore.GetAsync(cancellationToken);
                var target = await context.Collection<Streamer>("streamers").Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
                if (target is null || !target.Enabled || !target.IsMonitored)
                {
                    break;
                }
                try
                {
                    var room = await bilibili.GetRoomAsync(target.RoomId, target.AccountId, cancellationToken);
                    await UpdateTargetAsync(target, room, room.IsLive ? "Recording" : "Waiting", null, cancellationToken);
                    if (!room.IsLive)
                    {
                        offlineSince ??= DateTime.UtcNow;
                        if (session is not null && DateTime.UtcNow - offlineSince >= TimeSpan.FromMinutes(settings.SessionGraceMinutes))
                        {
                            await StopEventCaptureAsync(eventCapture);
                            eventCapture = null;
                            await CloseSessionAsync(session, offlineSince.Value, cancellationToken);
                            session = null;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(settings.MonitorIntervalSeconds), cancellationToken);
                        continue;
                    }
                    var liveTime = room.LiveStartedAtUtc is { } liveAt ? new DateTimeOffset(liveAt).ToUnixTimeSeconds() : 0;
                    if (session is not null && session.LiveTime > 0 && liveTime > 0 && session.LiveTime != liveTime)
                    {
                        await StopEventCaptureAsync(eventCapture);
                        eventCapture = null;
                        await CloseSessionAsync(session, offlineSince ?? session.LastMediaAtUtc, cancellationToken);
                        session = null;
                    }
                    offlineSince = null;
                    if (session is null)
                    {
                        session = await CreateSessionAsync(target, room, liveTime, settings, cancellationToken);
                    }
                    var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
                    if ((target.RecordDanmu ?? settings.RecordDanmu) && eventCapture is null)
                    {
                        eventCapture = new SessionEventCapture(session, target, directory, CaptureEventsAsync, cancellationToken);
                    }
                    if (AvailableBytes(paths.RecordsDirectory) < settings.MinimumFreeBytes)
                    {
                        throw new InfrastructureException(MessageCode.RECORDING_DISK_SPACE_LOW, 507);
                    }
                    var source = await bilibili.ResolveStreamAsync(target.RoomId, target.Quality ?? settings.Quality, target.AccountId, cancellationToken);
                    var attemptId = Guid.NewGuid().ToString("N");
                    var sessionId = session.Id;
                    var existing = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == sessionId).ToListAsync(cancellationToken);
                    await using var sink = new RecordingProgressSink(session, existing, attemptId, directory, context, publisher, logger);
                    await recorder.RecordAsync(new RecordingRequest
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
                    }, sink, cancellationToken);
                    retrySeconds = 2;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    target.State = "Retrying";
                    target.Error = error.Message;
                    target.ErrorCode = (error as InfrastructureException)?.Code ?? MessageCode.RECORDING_CAPTURE_FAILED.ToString();
                    target.ErrorArguments = error is InfrastructureException { Code: not null } businessError ? businessError.Arguments.ToList() : [error.Message];
                    if (session is not null)
                    {
                        session.Error = error.Message;
                        session.ErrorCode = target.ErrorCode;
                        session.ErrorArguments = target.ErrorArguments.ToList();
                    }
                    await TrySaveTargetAsync(target, cancellationToken);
                    logger.LogWarning("直播间 {RoomId} 等待重试：{Message}", target.RoomId, error.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken);
                retrySeconds = Math.Min(30, retrySeconds * 2);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopEventCaptureAsync(eventCapture);
            if (session is not null)
            {
                try
                {
                    var target = await context.Collection<Streamer>("streamers").Find(x => x.Id == id).FirstOrDefaultAsync(CancellationToken.None);
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
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task<BroadcastSession> CreateSessionAsync(Streamer target, LiveRoomSnapshot room, long liveTime, ServerSettings settings, CancellationToken cancellationToken)
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
            HasDanmu = target.RecordDanmu ?? settings.RecordDanmu
        };
        var name = TimeZoneInfo.ConvertTimeFromUtc(now, zone).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var streamerDirectory = string.IsNullOrEmpty(target.DirectoryName) ? MediaPaths.SafeName(room.Name) + "_" + room.Uid : target.DirectoryName;
        target.DirectoryName = streamerDirectory;
        session.DirectoryPath = Path.Combine(streamerDirectory, name + "_" + MediaPaths.SafeName(room.Title) + "_" + session.Id[..8]);
        var directory = MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath);
        Directory.CreateDirectory(directory);
        await JsonFiles.WriteAsync(Path.Combine(directory, "场次.json"), new SessionManifest { Session = session }, cancellationToken);
        await JsonFiles.WriteAsync(Path.Combine(paths.RecordsDirectory, streamerDirectory, "主播信息.json"), room, cancellationToken);
        await context.Collection<BroadcastSession>("sessions").InsertOneAsync(session, cancellationToken: cancellationToken);
        await TrySaveTargetAsync(target, cancellationToken);
        await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
        return session;
    }

    private async Task CloseSessionAsync(BroadcastSession session, DateTime end, CancellationToken cancellationToken)
    {
        session.EndedAtUtc = end < session.LastMediaAtUtc ? session.LastMediaAtUtc : end;
        session.State = "Completed";
        var segments = await context.Collection<RecordingSegment>("segments").Find(x => x.SessionId == session.Id).ToListAsync(cancellationToken);
        var current = await context.Collection<BroadcastSession>("sessions").FindOneAndUpdateAsync(x => x.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.EndedAtUtc, session.EndedAtUtc).Set(x => x.State, session.State),
            new FindOneAndUpdateOptions<BroadcastSession> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        await JsonFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath), "场次.json"), new SessionManifest { Session = current ?? session, Segments = segments }, cancellationToken);
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
        foreach (var path in Directory.EnumerateFiles(paths.RecordsDirectory, "场次.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
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
                    segment.IsClosed = true;
                    await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(x => x.Id == segment.Id, segment, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                }
            }
            catch (JsonException error)
            {
                logger.LogWarning("档案清单 {Path} 无法读取：{Message}", path, error.Message);
            }
        }
        await grouping.ReconcileAsync(cancellationToken);
        var unfinished = await context.Collection<BroadcastSession>("sessions").Find(x => x.EndedAtUtc == null && !x.IsHidden).ToListAsync(cancellationToken);
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
