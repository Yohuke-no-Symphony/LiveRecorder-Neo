using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证多个物理采集来源对同一逻辑场次的持续投影。</summary>
public sealed class RecordingLogicalSessionTests
{
    /// <summary>暂停并交接后保留播放身份和独立弹幕时间映射。</summary>
    [MongoHttpFact]
    public async Task PausedCapturesRetainOneLogicalSessionAndIndependentEventSources()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var projection = fixture.App.Services.GetRequiredService<RecordingSessionProjectionService>();
        var started = DateTime.UtcNow;
        var session = new BroadcastSession
        {
            Id = "logical-session", StreamerId = "streamer", RoomId = 123, StartedAtUtc = started,
            LastMediaAtUtc = started, DirectoryPath = "streamer/logical-session",
            Lifecycle = new() { TimeoutAnchorUtc = started, LastMediaReceivedAtUtc = started, LastObservedIsLive = true }
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await projection.RegisterLocalCaptureAsync(session, "first-capture", session.DirectoryPath, started, "first-assignment", 1, CancellationToken.None);
        var first = new RecordingSegment
        {
            Id = "first-segment", SessionId = session.Id, StartOffsetMs = 0, DurationMs = 1000,
            RelativePath = "录像/first.flv", IsClosed = true, CommittedBytes = 100
        };
        await projection.PublishLocalAsync(session, [first], "first-capture", true, CancellationToken.None);
        var continuation = await projection.GetContinuationAsync(session.StreamerId, CancellationToken.None);
        Assert.NotNull(continuation);
        Assert.Equal(session.Id, continuation.LogicalSessionId);

        var nextStart = started.AddSeconds(2);
        await projection.RegisterLocalCaptureAsync(session, "second-capture", session.DirectoryPath + "/.captures/second-capture", nextStart,
            "second-assignment", 2, CancellationToken.None);
        var second = new RecordingSegment
        {
            Id = "second-segment", SessionId = session.Id, StartOffsetMs = 2000, DurationMs = 1000,
            RelativePath = "录像/second.flv", RootRelativePath = session.DirectoryPath + "/.captures/second-capture/录像/second.flv",
            IsClosed = true, CommittedBytes = 150
        };
        await projection.PublishLocalAsync(session, [second], "second-capture", true, CancellationToken.None);

        var saved = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == session.Id).SingleAsync();
        Assert.Null(saved.EndedAtUtc);
        Assert.Equal(250, saved.TotalBytes);
        Assert.Equal(2, saved.EventSources.Count);
        Assert.Equal(2000, saved.EventSources[1].StartOffsetMs);
        Assert.Equal(2, await fixture.Context.Collection<RecordingSegment>("segments").CountDocumentsAsync(value => value.SessionId == session.Id));
        Assert.Single(await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.RoomId == session.RoomId).ToListAsync());
    }

    /// <summary>采集绑定一旦建立便不能被其它逻辑场次或执行代次复用。</summary>
    [MongoHttpFact]
    public async Task CaptureBindingCannotBeReassigned()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        var projection = fixture.App.Services.GetRequiredService<RecordingSessionProjectionService>();
        var session = new BroadcastSession { Id = "logical-one", RoomId = 123, StartedAtUtc = DateTime.UtcNow, DirectoryPath = "one" };
        await projection.RegisterLocalCaptureAsync(session, "capture", "one", session.StartedAtUtc, "assignment", 1, CancellationToken.None);
        session.Id = "logical-two";

        await Assert.ThrowsAsync<Yohuke.LiveRecorder.Application.InfrastructureException>(() => projection.RegisterLocalCaptureAsync(session,
            "capture", "two", session.StartedAtUtc, "assignment", 1, CancellationToken.None));
    }

    /// <summary>远端接续使用独立收据和嵌套目录，不能改写最初的逻辑直播身份。</summary>
    [MongoHttpFact]
    public async Task RemoteContinuationKeepsItsOwnIngestionFenceAndPhysicalDirectory()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var worker = (await fixture.App.Services.GetRequiredService<TranscodeWorkerService>()
            .CreateAsync("逻辑接续节点", "Recording", CancellationToken.None)).Worker;
        var target = new Streamer { RoomId = 74123, Name = "接续主播" };
        await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(target);
        var started = DateTime.UtcNow;
        var logical = new BroadcastSession
        {
            Id = "continued-session", StreamerId = target.Id, RoomId = target.RoomId, StartedAtUtc = started,
            LastMediaAtUtc = started, DirectoryPath = "streamer/continued-session", State = "Waiting",
            Lifecycle = new() { LastMediaReceivedAtUtc = started, TimeoutAnchorUtc = started, LastObservedIsLive = true }
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(logical);
        var ownership = fixture.App.Services.GetRequiredService<RecordingOwnershipService>();
        var assignment = (await ownership.TryAcquireWorkerAsync(target, worker.Id, CancellationToken.None))!;
        await fixture.Context.Collection<RecordingAssignmentRecord>("recordingAssignments").UpdateOneAsync(value => value.Id == assignment.Id,
            Builders<RecordingAssignmentRecord>.Update.Set(value => value.Definition, new RecordingAssignment
            {
                Continuation = new() { LogicalSessionId = logical.Id, StartedAtUtc = started, Lifecycle = logical.Lifecycle }
            }));
        var descriptor = new RecordingSessionDescriptor
        {
            SessionId = "physical-continuation", LogicalSessionId = logical.Id, LogicalStartedAtUtc = started,
            RoomId = target.RoomId, StartedAtUtc = started.AddSeconds(1), Lifecycle = logical.Lifecycle
        };
        var ingestion = fixture.App.Services.GetRequiredService<RecordingIngestService>();
        await ingestion.RegisterSessionAsync(worker.Id, assignment.Id, assignment.Generation, ownership.Token(assignment), descriptor, CancellationToken.None);
        await ingestion.RegisterSessionAsync(worker.Id, assignment.Id, assignment.Generation, ownership.Token(assignment), descriptor, CancellationToken.None);

        var receipt = await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions")
            .Find(value => value.Id == descriptor.SessionId).SingleAsync();
        Assert.Equal(Path.Combine(logical.DirectoryPath, ".captures", descriptor.SessionId), receipt.DirectoryPath);
        var binding = await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
            .Find(value => value.Id == descriptor.SessionId).SingleAsync();
        Assert.Equal(logical.Id, binding.LogicalSessionId);
        Assert.Equal(assignment.Id, binding.AssignmentId);
        Assert.Single(await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.RoomId == target.RoomId).ToListAsync());
        await Assert.ThrowsAsync<Yohuke.LiveRecorder.Application.InfrastructureException>(() => ingestion.RegisterSessionAsync(worker.Id,
            assignment.Id, assignment.Generation, ownership.Token(assignment), descriptor with { LogicalSessionId = "other-logical" }, CancellationToken.None));
    }

    /// <summary>心跳早于新采集归档注册到达时，不能把尚未登记的来源遗漏后提前完成逻辑场次。</summary>
    [MongoHttpFact]
    public Task TerminalHeartbeatCannotFinishBeforeItsCaptureIsRegistered() => VerifyUnregisteredCaptureHeartbeatAsync(false);

    /// <summary>恢复节点省略当前采集标识时，仍通过同批媒体来源防止提前完成逻辑场次。</summary>
    [MongoHttpFact]
    public Task TerminalHeartbeatWithoutCurrentCaptureCannotOmitItsDurableMedia() => VerifyUnregisteredCaptureHeartbeatAsync(true);

    private static async Task VerifyUnregisteredCaptureHeartbeatAsync(bool omitCurrentCapture)
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var worker = (await fixture.App.Services.GetRequiredService<TranscodeWorkerService>()
            .CreateAsync("终态乱序节点", "Recording", CancellationToken.None)).Worker;
        await fixture.Context.Collection<TranscodeWorker>("transcodeWorkers").UpdateOneAsync(value => value.Id == worker.Id,
            Builders<TranscodeWorker>.Update.Set(value => value.Capabilities, new WorkerCapabilities { RecordingVersion = 2 }));
        var target = new Streamer { RoomId = 74124, Name = "乱序主播" };
        await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(target);
        var started = DateTime.UtcNow;
        var logical = new BroadcastSession
        {
            Id = "pending-terminal-session", StreamerId = target.Id, RoomId = target.RoomId, StartedAtUtc = started,
            LastMediaAtUtc = started, DirectoryPath = "streamer/pending-terminal-session", State = "Waiting",
            Lifecycle = new() { LastMediaReceivedAtUtc = started, TimeoutAnchorUtc = started, LastObservedIsLive = true }
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(logical);
        var projection = fixture.App.Services.GetRequiredService<RecordingSessionProjectionService>();
        await projection.RegisterLocalCaptureAsync(logical, "drained-local", logical.DirectoryPath, started, "local", 1, CancellationToken.None);
        await projection.PublishLocalAsync(logical, [], "drained-local", true, CancellationToken.None);
        var ownership = fixture.App.Services.GetRequiredService<RecordingOwnershipService>();
        var assignment = (await ownership.TryAcquireWorkerAsync(target, worker.Id, CancellationToken.None))!;
        var continuation = new RecordingSessionContinuation { LogicalSessionId = logical.Id, StartedAtUtc = started, Lifecycle = logical.Lifecycle };
        await fixture.Context.Collection<RecordingAssignmentRecord>("recordingAssignments").UpdateOneAsync(value => value.Id == assignment.Id,
            Builders<RecordingAssignmentRecord>.Update.Set(value => value.Definition, new RecordingAssignment { Continuation = continuation }));
        var heartbeat = new WorkerHeartbeat
        {
            RecordingSessions = omitCurrentCapture ? [new()
            {
                AssignmentId = assignment.Id, Generation = assignment.Generation, Token = ownership.Token(assignment),
                Descriptor = new()
                {
                    SessionId = "not-yet-registered", LogicalSessionId = logical.Id, LogicalStartedAtUtc = started,
                    RoomId = target.RoomId, StartedAtUtc = started.AddSeconds(1), IsClosed = true, EndedAtUtc = started.AddSeconds(2)
                },
                CaptureState = "Closed", CapturedDurationMs = 1000, CommittedArchiveBytes = 100
            }] : [],
            Recordings = [new()
            {
                AssignmentId = assignment.Id, Generation = assignment.Generation, Token = ownership.Token(assignment),
                SessionId = omitCurrentCapture ? null : "not-yet-registered", State = "Monitoring",
                Continuation = continuation with
                {
                    Lifecycle = logical.Lifecycle with { Revision = 13, EndReason = RecordingSessionEndReason.MediaTimeout, EndedAtUtc = started.AddMinutes(5) }
                }
            }]
        };
        var scheduler = fixture.App.Services.GetRequiredService<RecordingJobScheduler>();
        await scheduler.HeartbeatAsync(worker.Id, heartbeat, CancellationToken.None);

        var saved = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == logical.Id).SingleAsync();
        Assert.Null(saved.EndedAtUtc);
        Assert.Equal(RecordingSessionEndReason.None, saved.Lifecycle!.EndReason);
        Assert.True(saved.Lifecycle.Revision < 13);
        await fixture.App.Services.GetRequiredService<RecordingIngestService>().RegisterSessionAsync(worker.Id, assignment.Id, assignment.Generation,
            ownership.Token(assignment), new()
            {
                SessionId = "not-yet-registered", LogicalSessionId = logical.Id, LogicalStartedAtUtc = started,
                RoomId = target.RoomId, StartedAtUtc = started.AddSeconds(1), Lifecycle = heartbeat.Recordings[0].Continuation!.Lifecycle
            }, CancellationToken.None);
        await scheduler.HeartbeatAsync(worker.Id, heartbeat, CancellationToken.None);

        var finalizing = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == logical.Id).SingleAsync();
        Assert.Equal(13, finalizing.Lifecycle!.Revision);
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, finalizing.Lifecycle.EndReason);
        Assert.Equal("Finalizing", finalizing.State);
        Assert.Null(finalizing.EndedAtUtc);
    }

    /// <summary>服务器缺少新进度不能结束仍被离线节点持有的房间，释放后才推进暂停期限。</summary>
    [MongoHttpFact]
    public async Task PausedMaintenanceDoesNotExpireADisconnectedOwner()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var started = DateTime.UtcNow.AddHours(-1);
        var logical = new BroadcastSession
        {
            Id = "disconnected-session", StreamerId = "disconnected-target", RoomId = 74125, StartedAtUtc = started,
            LastMediaAtUtc = started, DirectoryPath = "streamer/disconnected-session",
            Lifecycle = new() { LastMediaReceivedAtUtc = started, TimeoutAnchorUtc = started, LastObservedIsLive = true }
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(logical);
        var projection = fixture.App.Services.GetRequiredService<RecordingSessionProjectionService>();
        await projection.RegisterLocalCaptureAsync(logical, "previous-local", logical.DirectoryPath, started, "previous-owner", 1, CancellationToken.None);
        await projection.PublishLocalAsync(logical,
            [new() { Id = "previous-segment", SessionId = logical.Id, DurationMs = 1000, CommittedBytes = 100, IsClosed = true }],
            "previous-local", true, CancellationToken.None);
        var owner = new RecordingOwnership
        {
            Id = "recording-room-74125", RoomId = logical.RoomId, TargetId = logical.StreamerId,
            AssignmentId = "offline-owner", OwnerKind = "Worker", OwnerId = "offline-worker", Generation = 2, State = "Owned"
        };
        await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships").InsertOneAsync(owner);

        await projection.MaintainPausedAsync(CancellationToken.None);

        var retained = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == logical.Id).SingleAsync();
        Assert.Null(retained.EndedAtUtc);
        Assert.Equal(RecordingSessionEndReason.None, retained.Lifecycle!.EndReason);
        await fixture.Context.Collection<RecordingOwnership>("recordingOwnerships").UpdateOneAsync(value => value.Id == owner.Id,
            Builders<RecordingOwnership>.Update.Set(value => value.State, "Released"));

        await projection.MaintainPausedAsync(CancellationToken.None);

        var ended = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == logical.Id).SingleAsync();
        Assert.Equal("Completed", ended.State);
        Assert.NotNull(ended.EndedAtUtc);
        Assert.Equal(RecordingSessionEndReason.MediaTimeout, ended.Lifecycle!.EndReason);
    }

    /// <summary>较旧归档快照不能清除已收到的下播观察，高版本真实媒体时间在系统回拨后仍被原样保存。</summary>
    [MongoHttpFact]
    public async Task LifecycleRevisionsPreserveOfflineObservationsAndClockRollbackActivity()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcDateTime;
        var initial = new RecordingSessionLifecycleState { Revision = 1, LastMediaReceivedAtUtc = now, TimeoutAnchorUtc = now };
        var session = new BroadcastSession
        {
            Id = "revision-session", StreamerId = "revision-target", RoomId = 74126, StartedAtUtc = now.AddHours(-2),
            LastMediaAtUtc = now, LastMediaReceivedAtUtc = now, DirectoryPath = "streamer/revision-session", Lifecycle = initial
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        var projection = fixture.App.Services.GetRequiredService<RecordingSessionProjectionService>();
        var offline = initial with { Revision = 2, OfflineSinceUtc = now, LastObservationAtUtc = now, LastObservedIsLive = false };
        await projection.SynchronizeLifecycleAsync(session.Id, offline, CancellationToken.None);
        await projection.SynchronizeLifecycleAsync(session.Id, initial, CancellationToken.None);
        var waiting = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == session.Id).SingleAsync();
        Assert.Equal(now, waiting.Lifecycle!.OfflineSinceUtc);
        Assert.Equal(2, waiting.Lifecycle.Revision);

        var rolledBack = now.AddHours(-1);
        var resumed = offline with
        {
            Revision = 3, LastMediaReceivedAtUtc = rolledBack, OfflineSinceUtc = null,
            LastObservationAtUtc = rolledBack, LastObservedIsLive = true
        };
        await projection.SynchronizeLifecycleAsync(session.Id, resumed, CancellationToken.None);
        await projection.SynchronizeLifecycleAsync(session.Id, offline, CancellationToken.None);
        var current = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == session.Id).SingleAsync();
        Assert.Equal(3, current.Lifecycle!.Revision);
        Assert.Equal(rolledBack, current.Lifecycle.LastMediaReceivedAtUtc);
        Assert.Equal(rolledBack, current.LastMediaReceivedAtUtc);
        Assert.Null(current.Lifecycle.OfflineSinceUtc);
    }

    /// <summary>新节点恢复旧活动收据时升级为自身逻辑映射，物理停录不会再次被误判为直播结束。</summary>
    [MongoHttpFact]
    public async Task UpgradedLegacyCaptureCanPauseWithoutEndingItsLogicalSession()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var worker = (await fixture.App.Services.GetRequiredService<TranscodeWorkerService>()
            .CreateAsync("旧采集升级节点", "Recording", CancellationToken.None)).Worker;
        var target = new Streamer { RoomId = 74127, Name = "旧采集主播" };
        await fixture.Context.Collection<Streamer>("streamers").InsertOneAsync(target);
        var ownership = fixture.App.Services.GetRequiredService<RecordingOwnershipService>();
        var assignment = (await ownership.TryAcquireWorkerAsync(target, worker.Id, CancellationToken.None))!;
        var started = DateTime.UtcNow;
        var descriptor = new RecordingSessionDescriptor { SessionId = "legacy-capture", RoomId = target.RoomId, StartedAtUtc = started };
        await fixture.Context.Collection<RecordingSessionReceipt>("recordingSessions").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, AssignmentId = assignment.Id, Generation = assignment.Generation,
            Descriptor = descriptor, DirectoryPath = "streamer/legacy-capture", Published = true, LibraryPublished = true
        });
        await fixture.Context.Collection<RecordingSegmentReceipt>("recordingSegments").InsertOneAsync(new()
        {
            Id = "legacy-segment", SessionId = descriptor.SessionId, AssignmentId = assignment.Id, Generation = assignment.Generation,
            Commit = new() { SegmentId = "legacy-segment", DurationMs = 1000, MediaCommittedBytes = 100, IsClosed = true }
        });
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            Id = "legacy-segment", SessionId = descriptor.SessionId, DurationMs = 1000, CommittedBytes = 100, IsClosed = true
        });
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(new()
        {
            Id = descriptor.SessionId, StreamerId = target.Id, RoomId = target.RoomId, StartedAtUtc = started,
            LastMediaAtUtc = started.AddSeconds(1), DirectoryPath = "streamer/legacy-capture", RecordingAssignmentId = assignment.Id
        });

        await fixture.App.Services.GetRequiredService<RecordingIngestService>().RegisterSessionAsync(worker.Id, assignment.Id, assignment.Generation,
            ownership.Token(assignment), descriptor with
            {
                IsClosed = true, EndedAtUtc = started.AddSeconds(1),
                Lifecycle = new() { Revision = 1, LastMediaReceivedAtUtc = started, TimeoutAnchorUtc = started }
            }, CancellationToken.None);

        var saved = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == descriptor.SessionId).SingleAsync();
        Assert.Equal("Waiting", saved.State);
        Assert.Null(saved.EndedAtUtc);
        Assert.Equal(RecordingSessionEndReason.None, saved.Lifecycle!.EndReason);
        Assert.Single(await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
            .Find(value => value.LogicalSessionId == saved.Id).ToListAsync());
    }

    /// <summary>旧本地场次没有采集映射时，从原有已封口片段恢复来源后可以正常完成。</summary>
    [MongoHttpFact]
    public async Task LegacyLocalSessionCanFinalizeWithoutAnExistingCaptureBinding()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        await fixture.InstallAsync();
        await fixture.App.Services.GetServices<IHostedService>().OfType<RecordingSupervisor>().Single().StopAsync(CancellationToken.None);
        var started = DateTime.UtcNow.AddMinutes(-10);
        var session = new BroadcastSession
        {
            Id = "legacy-local", RoomId = 74128, StreamerId = "legacy-local-target", StartedAtUtc = started,
            LastMediaAtUtc = started.AddSeconds(1), DirectoryPath = "streamer/legacy-local"
        };
        await fixture.Context.Collection<BroadcastSession>("sessions").InsertOneAsync(session);
        await fixture.Context.Collection<RecordingSegment>("segments").InsertOneAsync(new()
        {
            Id = "legacy-local-media", SessionId = session.Id, DurationMs = 1000, CommittedBytes = 100, IsClosed = true
        });
        await fixture.App.Services.GetRequiredService<RecordingSessionProjectionService>().SynchronizeLifecycleAsync(session.Id, new()
        {
            Revision = 1, LastMediaReceivedAtUtc = started, TimeoutAnchorUtc = started,
            EndReason = RecordingSessionEndReason.MediaTimeout, EndedAtUtc = started.AddMinutes(5)
        }, CancellationToken.None);

        var saved = await fixture.Context.Collection<BroadcastSession>("sessions").Find(value => value.Id == session.Id).SingleAsync();
        Assert.Equal("Completed", saved.State);
        Assert.NotNull(saved.EndedAtUtc);
        var binding = await fixture.Context.Collection<RecordingCaptureBinding>("recordingCaptureBindings")
            .Find(value => value.LogicalSessionId == session.Id).SingleAsync();
        Assert.Equal(session.DirectoryPath, binding.DirectoryPath);
        Assert.True(binding.IsClosed);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<SessionManifest>(await File.ReadAllTextAsync(
            Path.Combine(fixture.StoragePath, session.DirectoryPath, "场次.json")), JsonFiles.Options)!;
        Assert.Equal(saved.EndedAtUtc, manifest.Session.EndedAtUtc);
        Assert.Equal(saved.Lifecycle!.EndedAtUtc, manifest.Session.Lifecycle!.EndedAtUtc);
    }
}
