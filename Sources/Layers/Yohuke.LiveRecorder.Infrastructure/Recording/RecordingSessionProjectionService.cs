using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Recording.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>固定采集来源归属，并将独立文件和事件序号投影到稳定的逻辑场次。</summary>
public sealed class RecordingSessionProjectionService(IMongoContext context, IAppPaths paths, ISettingsStore settingsStore,
    IStatusPublisher publisher, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private IMongoCollection<RecordingCaptureBinding> Captures => context.Collection<RecordingCaptureBinding>("recordingCaptureBindings");
    private IMongoCollection<BroadcastSession> Sessions => context.Collection<BroadcastSession>("sessions");

    /// <summary>返回仍可继续的逻辑场次；暂停期间已到期的场次不会再次授予采集者。</summary>
    public async Task<RecordingSessionContinuation?> GetContinuationAsync(string targetId, CancellationToken cancellationToken)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var session = await Sessions.Find(value => value.StreamerId == targetId && value.EndedAtUtc == null
                && !value.IsHidden && !value.IsTrashed && value.ProjectionReady)
            .SortByDescending(value => value.StartedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return null;
        }
        var originalState = session.Lifecycle;
        EnsureLifecycle(session);
        await EnsureLegacyCaptureAsync(session, cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);
        var owned = await context.Collection<RecordingOwnership>("recordingOwnerships")
            .Find(value => value.RoomId == session.RoomId && value.State != "Released").AnyAsync(cancellationToken);
        if (!owned)
        {
            var lifecycle = new RecordingSessionLifecycle(session.Lifecycle!, clock, settings.SessionGraceMinutes, settings.StreamDisconnectTimeoutMinutes);
            lifecycle.Evaluate();
            session.Lifecycle = lifecycle.State;
        }
        if (session.Lifecycle!.EndReason != RecordingSessionEndReason.None)
        {
            await ProjectAsync(session, cancellationToken);
            return null;
        }
        if (session.Lifecycle != originalState)
        {
            await SaveSessionAsync(session, cancellationToken);
        }
        return new() { LogicalSessionId = session.Id, StartedAtUtc = session.StartedAtUtc, Lifecycle = session.Lifecycle };
    }

    /// <summary>幂等登记本地采集来源，拒绝将既有采集身份重新分配。</summary>
    public async Task<RecordingCaptureBinding> RegisterLocalCaptureAsync(BroadcastSession logicalSession, string captureId,
        string directoryPath, DateTime captureStartedAtUtc, string? assignmentId, int generation, CancellationToken cancellationToken)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        return await RegisterCaptureAsync(new()
        {
            Id = captureId, LogicalSessionId = logicalSession.Id, AssignmentId = assignmentId, Generation = generation,
            DirectoryPath = directoryPath, StartedAtUtc = captureStartedAtUtc, IsLocal = true, HasDanmu = logicalSession.HasDanmu,
            LastMediaReceivedAtUtc = logicalSession.LastMediaReceivedAtUtc, LifecycleRevision = logicalSession.Lifecycle?.Revision ?? 0
        }, cancellationToken);
    }

    /// <summary>提交已经使用逻辑偏移的本地媒体，并独立记录物理采集是否封口。</summary>
    public async Task PublishLocalAsync(BroadcastSession logicalSession, IReadOnlyList<RecordingSegment> logicalSegments,
        string captureId, bool captureClosed, CancellationToken cancellationToken)
    {
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var capture = await Captures.Find(value => value.Id == captureId && value.LogicalSessionId == logicalSession.Id && value.IsLocal)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new InfrastructureException("本地采集来源尚未登记。", 409);
        foreach (var segment in logicalSegments)
        {
            if (segment.SessionId != logicalSession.Id)
            {
                throw new InfrastructureException("录像片段不属于此逻辑场次。", 409);
            }
            await context.Collection<RecordingSegment>("segments").ReplaceOneAsync(value => value.Id == segment.Id, segment,
                new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
        capture.IsClosed = captureClosed;
        capture.HasDanmu |= logicalSession.HasDanmu;
        ApplyCaptureActivity(capture, logicalSession.Lifecycle, logicalSession.LastMediaReceivedAtUtc);
        await Captures.ReplaceOneAsync(value => value.Id == capture.Id, capture, cancellationToken: cancellationToken);
        var existing = await Sessions.Find(value => value.Id == logicalSession.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            RequireWritable(existing);
            existing.Lifecycle = MergeLifecycle(existing.Lifecycle, logicalSession.Lifecycle);
            logicalSession = existing;
        }
        logicalSession.LastMediaReceivedAtUtc = logicalSession.Lifecycle?.LastMediaReceivedAtUtc
            ?? Maximum(logicalSession.LastMediaReceivedAtUtc, capture.LastMediaReceivedAtUtc);
        await ProjectAsync(logicalSession, cancellationToken);
    }

    /// <summary>保存当前逻辑直播的观察状态，物理来源封口不自行结束逻辑场次。</summary>
    public async Task SynchronizeLifecycleAsync(string logicalId, RecordingSessionLifecycleState state, CancellationToken cancellationToken)
    {
        ValidateLifecycle(state);
        await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
        var session = await Sessions.Find(value => value.Id == logicalId).FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return;
        }
        RequireWritable(session);
        session.Lifecycle = MergeLifecycle(session.Lifecycle, state);
        session.LastMediaReceivedAtUtc = session.Lifecycle?.LastMediaReceivedAtUtc;
        await ProjectAsync(session, cancellationToken);
    }

    /// <summary>取得不可变的采集归属，供清单重建和本地接续使用。</summary>
    public async Task<IReadOnlyList<RecordingCaptureBinding>> GetBindingsAsync(string logicalId, CancellationToken cancellationToken) =>
        await Captures.Find(value => value.LogicalSessionId == logicalId).SortBy(value => value.StartedAtUtc).ThenBy(value => value.Id).ToListAsync(cancellationToken);

    /// <summary>仅在没有未释放采集所有者时推进暂停场次，失联节点仍拥有其房间。</summary>
    public async Task MaintainPausedAsync(CancellationToken cancellationToken)
    {
        var candidates = await Sessions.Find(value => value.EndedAtUtc == null && value.Lifecycle != null
                && !value.IsHidden && !value.IsTrashed && value.ProjectionReady)
            .SortBy(value => value.StartedAtUtc).ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            await using var roomLock = await MediaJobExecutionLock.AcquireAsync(paths,
                "recording-room-" + candidate.RoomId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            await using var libraryLock = await SessionLibraryLock.AcquireAsync(paths.RecordsDirectory, cancellationToken);
            if (await context.Collection<RecordingOwnership>("recordingOwnerships")
                .Find(value => value.RoomId == candidate.RoomId && value.State != "Released").AnyAsync(cancellationToken))
            {
                continue;
            }
            var captures = await GetBindingsAsync(candidate.Id, cancellationToken);
            if (captures.Count > 0 && captures.Any(value => !value.IsClosed))
            {
                continue;
            }
            var session = await Sessions.Find(value => value.Id == candidate.Id && value.EndedAtUtc == null && !value.IsTrashed && !value.IsHidden)
                .FirstOrDefaultAsync(cancellationToken);
            if (session?.Lifecycle is null)
            {
                continue;
            }
            var settings = await settingsStore.GetAsync(cancellationToken);
            var lifecycle = new RecordingSessionLifecycle(session.Lifecycle, clock, settings.SessionGraceMinutes, settings.StreamDisconnectTimeoutMinutes);
            lifecycle.Evaluate();
            session.Lifecycle = lifecycle.State;
            await ProjectAsync(session, cancellationToken);
        }
    }

    internal async Task RegisterRemoteCaptureUnderLibraryLockAsync(RecordingAssignmentRecord assignment, RecordingSessionReceipt receipt,
        CancellationToken cancellationToken)
    {
        var descriptor = receipt.Descriptor;
        if (descriptor.Lifecycle is { } state)
        {
            ValidateLifecycle(state);
        }
        var logicalId = LogicalId(receipt);
        var existing = await Sessions.Find(value => value.Id == logicalId).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            RequireWritable(existing);
            var registered = await Captures.Find(value => value.Id == receipt.Id).AnyAsync(cancellationToken);
            if (!registered && (existing.EndedAtUtc is not null || existing.Lifecycle?.EndReason is { } ended && ended != RecordingSessionEndReason.None))
            {
                throw new InfrastructureException("此逻辑直播已经结束，不能增加采集来源。", 409);
            }
            if (existing.RoomId != assignment.RoomId || existing.StreamerId != assignment.TargetId
                || descriptor.LogicalStartedAtUtc is { } started && !SameInstant(started, existing.StartedAtUtc))
            {
                throw new InfrastructureException("逻辑场次不属于当前房间或开始时间已改变。", 409);
            }
            var authorized = assignment.Definition?.Continuation?.LogicalSessionId == logicalId
                || await Captures.Find(value => value.LogicalSessionId == logicalId && value.AssignmentId == assignment.Id
                    && value.Generation == assignment.Generation).AnyAsync(cancellationToken);
            if (!registered && logicalId != receipt.Id && !authorized)
            {
                throw new InfrastructureException("采集任务未获授权继续此逻辑场次。", 409);
            }
        }
        await RegisterCaptureAsync(new()
        {
            Id = receipt.Id, LogicalSessionId = logicalId, AssignmentId = assignment.Id, Generation = assignment.Generation,
            DirectoryPath = receipt.DirectoryPath, StartedAtUtc = descriptor.StartedAtUtc, HasDanmu = descriptor.HasDanmu,
            LastMediaReceivedAtUtc = descriptor.Lifecycle?.LastMediaReceivedAtUtc, LifecycleRevision = descriptor.Lifecycle?.Revision ?? 0
        }, cancellationToken);
    }

    internal async Task PublishRemoteUnderLibraryLockAsync(RecordingAssignmentRecord assignment, RecordingSessionReceipt receipt,
        bool sealedCapture, CancellationToken cancellationToken)
    {
        var descriptor = receipt.Descriptor;
        await RegisterRemoteCaptureUnderLibraryLockAsync(assignment, receipt, cancellationToken);
        var logicalId = LogicalId(receipt);
        var session = await Sessions.Find(value => value.Id == logicalId).FirstOrDefaultAsync(cancellationToken) ?? new BroadcastSession
        {
            Id = logicalId, StreamerId = assignment.TargetId, RoomId = assignment.RoomId, Title = descriptor.Title,
            CoverUrl = descriptor.CoverUrl, LiveTime = descriptor.LiveTime,
            StartedAtUtc = descriptor.LogicalStartedAtUtc ?? descriptor.StartedAtUtc,
            DirectoryPath = receipt.DirectoryPath, HasDanmu = descriptor.HasDanmu, AutoTranscodeEligible = true,
            RecordingAssignmentId = assignment.Id, RecordingGeneration = assignment.Generation
        };
        RequireWritable(session);
        var capture = await Captures.Find(value => value.Id == receipt.Id).SingleAsync(cancellationToken);
        capture.IsClosed = sealedCapture;
        capture.HasDanmu |= descriptor.HasDanmu;
        ApplyCaptureActivity(capture, descriptor.Lifecycle, null);
        if (descriptor.Lifecycle is null)
        {
            var commits = await context.Collection<RecordingSegmentReceipt>("recordingSegments")
                .Find(value => value.SessionId == receipt.Id && value.AssignmentId == assignment.Id).ToListAsync(cancellationToken);
            foreach (var committed in commits)
            {
                capture.LastMediaReceivedAtUtc = Maximum(capture.LastMediaReceivedAtUtc, committed.Commit?.LastMediaReceivedAtUtc);
            }
        }
        await Captures.ReplaceOneAsync(value => value.Id == capture.Id, capture, cancellationToken: cancellationToken);
        var ownsRoom = await context.Collection<RecordingOwnership>("recordingOwnerships")
            .Find(value => value.RoomId == assignment.RoomId && value.AssignmentId == assignment.Id
                && value.Generation == assignment.Generation && value.State != "Released").AnyAsync(cancellationToken);
        if (ownsRoom)
        {
            session.Lifecycle = MergeLifecycle(session.Lifecycle, descriptor.Lifecycle);
        }
        session.LastMediaReceivedAtUtc = session.Lifecycle?.LastMediaReceivedAtUtc
            ?? Maximum(session.LastMediaReceivedAtUtc, capture.LastMediaReceivedAtUtc);
        if (descriptor.LogicalSessionId is null && sealedCapture)
        {
            session.EndedAtUtc = descriptor.EndedAtUtc;
        }
        await ProjectAsync(session, cancellationToken);
    }

    internal static string LogicalId(RecordingSessionReceipt receipt) => receipt.Descriptor.LogicalSessionId ?? receipt.Id;

    private async Task<RecordingCaptureBinding> RegisterCaptureAsync(RecordingCaptureBinding capture, CancellationToken cancellationToken)
    {
        RecordingIngestPaths.RequireId(capture.Id);
        RecordingIngestPaths.RequireId(capture.LogicalSessionId);
        MediaPaths.Resolve(paths.RecordsDirectory, capture.DirectoryPath);
        var existing = await Captures.Find(value => value.Id == capture.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.LogicalSessionId != capture.LogicalSessionId || existing.AssignmentId != capture.AssignmentId
                || existing.Generation != capture.Generation || existing.DirectoryPath != capture.DirectoryPath
                || !SameInstant(existing.StartedAtUtc, capture.StartedAtUtc) || existing.IsLocal != capture.IsLocal)
            {
                throw new InfrastructureException("采集归属身份已改变。", 409);
            }
            return existing;
        }
        await Captures.InsertOneAsync(capture, cancellationToken: cancellationToken);
        return capture;
    }

    private async Task ProjectAsync(BroadcastSession session, CancellationToken cancellationToken)
    {
        await EnsureLegacyCaptureAsync(session, cancellationToken);
        var captures = await GetBindingsAsync(session.Id, cancellationToken);
        var segments = await context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == session.Id).ToListAsync(cancellationToken);
        session.LastMediaAtUtc = session.StartedAtUtc.AddMilliseconds(segments.Where(value => value.DurationMs > 0)
            .Select(value => value.StartOffsetMs + value.DurationMs).DefaultIfEmpty(0).Max());
        session.TotalBytes = segments.Sum(value => value.CommittedBytes);
        session.HasDanmu |= captures.Any(value => value.HasDanmu);
        session.EventSources = captures.Select(value => new EventArchiveSource
        {
            DirectoryPath = value.DirectoryPath, FromMs = 0,
            StartOffsetMs = (value.StartedAtUtc - session.StartedAtUtc).TotalMilliseconds
        }).ToList();
        var ended = session.Lifecycle?.EndReason is { } reason && reason != RecordingSessionEndReason.None || session.EndedAtUtc is not null;
        var sealedSources = captures.Count > 0 && captures.All(value => value.IsClosed) && segments.All(value => value.IsClosed);
        if (ended && sealedSources)
        {
            session.State = "Completed";
            session.EndedAtUtc = session.LastMediaAtUtc;
        }
        else
        {
            session.EndedAtUtc = null;
            session.State = ended ? "Finalizing" : session.Lifecycle?.LastObservedIsLive == false
                || captures.Count == 0 || captures.All(value => value.IsClosed) ? "Waiting" : "Recording";
        }
        await SaveSessionAsync(session, cancellationToken);
    }

    private async Task SaveSessionAsync(BroadcastSession session, CancellationToken cancellationToken)
    {
        session.CreatedAtUtc = MillisecondPrecision(session.CreatedAtUtc);
        session.StartedAtUtc = MillisecondPrecision(session.StartedAtUtc);
        session.LastMediaAtUtc = MillisecondPrecision(session.LastMediaAtUtc);
        session.EndedAtUtc = MillisecondPrecision(session.EndedAtUtc);
        session.LastMediaReceivedAtUtc = MillisecondPrecision(session.LastMediaReceivedAtUtc);
        if (session.Lifecycle is { } lifecycle)
        {
            session.Lifecycle = lifecycle with
            {
                LastMediaReceivedAtUtc = MillisecondPrecision(lifecycle.LastMediaReceivedAtUtc),
                TimeoutAnchorUtc = MillisecondPrecision(lifecycle.TimeoutAnchorUtc),
                OfflineSinceUtc = MillisecondPrecision(lifecycle.OfflineSinceUtc),
                LastObservationAtUtc = MillisecondPrecision(lifecycle.LastObservationAtUtc),
                EndedAtUtc = MillisecondPrecision(lifecycle.EndedAtUtc)
            };
        }
        var captures = await GetBindingsAsync(session.Id, cancellationToken);
        var segments = await context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == session.Id).ToListAsync(cancellationToken);
        await SessionManifestFiles.WriteAsync(Path.Combine(MediaPaths.Resolve(paths.RecordsDirectory, session.DirectoryPath), "场次.json"),
            new() { Session = session, Segments = segments, Captures = captures.ToList() }, cancellationToken);
        await Sessions.ReplaceOneAsync(value => value.Id == session.Id, session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        await publisher.PublishAsync("session", session.Id, cancellationToken: cancellationToken);
    }

    private void EnsureLifecycle(BroadcastSession session)
    {
        session.Lifecycle ??= new()
        {
            LastMediaReceivedAtUtc = session.LastMediaReceivedAtUtc, TimeoutAnchorUtc = clock.GetUtcNow().UtcDateTime,
            LiveTime = session.LiveTime
        };
    }

    private async Task EnsureLegacyCaptureAsync(BroadcastSession session, CancellationToken cancellationToken)
    {
        if (await Captures.Find(value => value.LogicalSessionId == session.Id).AnyAsync(cancellationToken))
        {
            return;
        }
        var segments = await context.Collection<RecordingSegment>("segments").Find(value => value.SessionId == session.Id).ToListAsync(cancellationToken);
        if (segments.Count == 0)
        {
            return;
        }
        var receipt = await context.Collection<RecordingSessionReceipt>("recordingSessions")
            .Find(value => value.Id == session.Id).FirstOrDefaultAsync(cancellationToken);
        await RegisterCaptureAsync(new()
        {
            Id = session.Id, LogicalSessionId = session.Id, AssignmentId = receipt?.AssignmentId, Generation = receipt?.Generation ?? 0,
            DirectoryPath = session.DirectoryPath, StartedAtUtc = session.StartedAtUtc, IsLocal = receipt is null,
            HasDanmu = session.HasDanmu, LastMediaReceivedAtUtc = session.LastMediaReceivedAtUtc,
            LifecycleRevision = session.Lifecycle?.Revision ?? 0,
            IsClosed = segments.All(value => value.IsClosed) && (receipt is null
                || receipt.Descriptor.IsClosed && (!receipt.Descriptor.HasDanmu || receipt.Events is { IsClosed: true }))
        }, cancellationToken);
    }

    private static RecordingSessionLifecycleState? MergeLifecycle(RecordingSessionLifecycleState? current, RecordingSessionLifecycleState? incoming)
    {
        if (incoming is null)
        {
            return current;
        }
        if (current is null)
        {
            return incoming with { };
        }
        if (current.EndReason != RecordingSessionEndReason.None)
        {
            return current;
        }
        if (current.Revision > 0 || incoming.Revision > 0)
        {
            return incoming.Revision > current.Revision ? incoming with { } : current;
        }
        if (current.LastObservationAtUtc is { } observed && (incoming.LastObservationAtUtc is null || incoming.LastObservationAtUtc < observed))
        {
            return current with { LastMediaReceivedAtUtc = Maximum(current.LastMediaReceivedAtUtc, incoming.LastMediaReceivedAtUtc) };
        }
        return incoming with { LastMediaReceivedAtUtc = Maximum(current.LastMediaReceivedAtUtc, incoming.LastMediaReceivedAtUtc) };
    }

    private static void ApplyCaptureActivity(RecordingCaptureBinding capture, RecordingSessionLifecycleState? state, DateTime? legacyActivity)
    {
        if (state is { Revision: > 0 })
        {
            if (state.Revision > capture.LifecycleRevision)
            {
                capture.LastMediaReceivedAtUtc = state.LastMediaReceivedAtUtc;
                capture.LifecycleRevision = state.Revision;
            }
            return;
        }
        if (capture.LifecycleRevision == 0)
        {
            capture.LastMediaReceivedAtUtc = Maximum(capture.LastMediaReceivedAtUtc, state?.LastMediaReceivedAtUtc ?? legacyActivity);
        }
    }

    private static void RequireWritable(BroadcastSession session)
    {
        if (session.IsHidden || session.IsTrashed || !session.ProjectionReady || session.MediaRevision != 1)
        {
            throw new InfrastructureException("逻辑场次已被移除或重新投影。", 410);
        }
    }

    private static void ValidateLifecycle(RecordingSessionLifecycleState state)
    {
        if (state.Revision < 0 || !Enum.IsDefined(state.EndReason))
        {
            throw new InfrastructureException("逻辑场次状态版本或结束原因无效。", 400);
        }
    }

    private static DateTime? Maximum(DateTime? left, DateTime? right) => left is null ? right : right is null || left >= right ? left : right;
    private static bool SameInstant(DateTime left, DateTime right) => left.Ticks / TimeSpan.TicksPerMillisecond == right.Ticks / TimeSpan.TicksPerMillisecond;
    private static DateTime MillisecondPrecision(DateTime value) => new(value.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, value.Kind);
    private static DateTime? MillisecondPrecision(DateTime? value) => value is { } instant ? MillisecondPrecision(instant) : null;
}
