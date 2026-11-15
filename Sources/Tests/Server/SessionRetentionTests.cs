using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>以真实档案与隔离数据库验证录像自动回收和永久保留。</summary>
public sealed class SessionRetentionTests
{
    /// <summary>只自动回收到期、可编辑且没有活动录制或作业的普通录像。</summary>
    [Theory]
    [InlineData("expired", true)]
    [InlineData("legacy", true)]
    [InlineData("recent", false)]
    [InlineData("permanent", false)]
    [InlineData("recording", false)]
    [InlineData("open-segment", false)]
    [InlineData("job", false)]
    [InlineData("unready", false)]
    [InlineData("disabled", false)]
    public async Task MaintenanceTrashesOnlyEligibleExpiredRecordings(string condition, bool expectedTrashed)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        fixture.Settings.Settings.RecordingRetentionDays = condition == "disabled" ? 0 : 7;
        session.StartedAtUtc = DateTime.UtcNow.AddDays(-11);
        session.EndedAtUtc = DateTime.UtcNow.AddDays(condition == "recent" ? -1 : -10);
        session.IsPermanent = condition == "permanent";
        session.ProjectionReady = condition != "unready";
        if (condition == "recording")
        {
            session.EndedAtUtc = null;
            session.State = "Recording";
        }
        if (condition == "open-segment")
        {
            await fixture.Mongo.Collection<RecordingSegment>("segments").UpdateOneAsync(x => x.Id == segments[0].Id,
                Builders<RecordingSegment>.Update.Set(x => x.IsClosed, false));
        }
        if (condition == "job")
        {
            await fixture.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(JobTestContext.CreateJob(session, segments));
        }
        await fixture.Mongo.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == session.Id, session);
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, session.DirectoryPath, "场次.json"),
            new SessionManifest { Session = session, Segments = segments });
        if (condition == "legacy")
        {
            await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
                Builders<BroadcastSession>.Update.Unset(x => x.IsPermanent).Unset(x => x.RetentionStartedAtUtc));
        }
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);

        await service.MaintainAsync();

        var current = await SessionAsync(fixture, session.Id);
        Assert.Equal(expectedTrashed, current.IsTrashed);
        var entries = await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == session.Id)
            .ToListAsync();
        if (expectedTrashed)
        {
            var entry = Assert.Single(entries);
            Assert.Equal("Session", entry.Kind);
            Assert.True(entry.PurgeAtUtc > DateTime.UtcNow.AddDays(6));
            Assert.True(File.Exists(Path.Combine(fixture.Root, entry.TrashRelativePath, segments[0].RelativePath)));
        }
        else
        {
            Assert.Empty(entries);
            Assert.True(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));
        }
    }

    /// <summary>恢复到期录像时重置保留起点，清单与数据库都能保留新的期限。</summary>
    [Fact]
    public async Task RestoredExpiredRecordingReceivesACompleteNewRetentionPeriod()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        fixture.Settings.Settings.RecordingRetentionDays = 7;
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.EndedAtUtc, DateTime.UtcNow.AddDays(-30)));
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.MaintainAsync();
        var entry = await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == session.Id)
            .SingleAsync();
        var beforeRestore = DateTime.UtcNow.AddSeconds(-1);

        await service.RestoreAsync(entry);
        await service.MaintainAsync();

        var restored = await SessionAsync(fixture, session.Id);
        Assert.False(restored.IsTrashed);
        Assert.True(restored.RetentionStartedAtUtc >= beforeRestore);
        var manifest = await ReadManifestAsync(fixture, session);
        Assert.False(manifest.Session.IsTrashed);
        Assert.Equal(restored.RetentionStartedAtUtc!.Value.ToString("yyyyMMddHHmmss"),
            manifest.Session.RetentionStartedAtUtc!.Value.ToString("yyyyMMddHHmmss"));
        Assert.Empty(await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == session.Id)
            .ToListAsync());
    }

    /// <summary>取消永久保留后获得完整的新周期，永久保留录像仍可手动删除。</summary>
    [Theory]
    [InlineData(30, 7)]
    [InlineData(1, 7)]
    [InlineData(30, 0)]
    public async Task PermanentFlagSkipsOnlyAutomaticDeletionAndCanBeRemoved(int daysSinceCompletion, int retentionDays)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        fixture.Settings.Settings.RecordingRetentionDays = retentionDays;
        session.EndedAtUtc = DateTime.UtcNow.AddDays(-daysSinceCompletion);
        session.RetentionStartedAtUtc = session.EndedAtUtc;
        await fixture.Mongo.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == session.Id, session);
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, session.DirectoryPath, "场次.json"),
            new SessionManifest { Session = session, Segments = segments });
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.SetPermanentAsync(session.Id, true);
        await service.MaintainAsync();
        Assert.False((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.True((await ReadManifestAsync(fixture, session)).Session.IsPermanent);
        var beforeCancellation = DateTime.UtcNow;

        var cancelled = await service.SetPermanentAsync(session.Id, false);
        await service.MaintainAsync();

        Assert.False(cancelled.IsPermanent);
        Assert.NotNull(cancelled.RetentionStartedAtUtc);
        Assert.InRange(cancelled.RetentionStartedAtUtc.Value, beforeCancellation, DateTime.UtcNow);
        var current = await SessionAsync(fixture, session.Id);
        Assert.False(current.IsTrashed);
        Assert.False(current.IsPermanent);
        Assert.InRange(current.RetentionStartedAtUtc!.Value,
            cancelled.RetentionStartedAtUtc.Value.AddMilliseconds(-1), cancelled.RetentionStartedAtUtc.Value);
        var manifest = await ReadManifestAsync(fixture, session);
        Assert.False(manifest.Session.IsPermanent);
        Assert.Equal(cancelled.RetentionStartedAtUtc, manifest.Session.RetentionStartedAtUtc);
        Assert.Empty(await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == session.Id).ToListAsync());
        Assert.True(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));

        fixture.Settings.Settings.RecordingRetentionDays = 7;
        await service.MaintainAsync();
        Assert.False((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.Equal(cancelled.RetentionStartedAtUtc.Value.AddDays(7),
            SessionRetentionPolicy.GetExpiresAtUtc(cancelled, fixture.Settings.Settings));
        await service.SetPermanentAsync(session.Id, true);
        await service.TrashAsync(session.Id, "member");
        Assert.True((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.True((await SessionAsync(fixture, session.Id)).IsPermanent);
    }

    /// <summary>重复取消沿用首次持久化的起点，并能修复尚未完成的数据库提交。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedCancellationPreservesTheDurableRetentionStart(bool databaseCommitInterrupted)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.SetPermanentAsync(session.Id, true);
        var cancelled = await service.SetPermanentAsync(session.Id, false);
        Assert.NotNull(cancelled.RetentionStartedAtUtc);
        if (databaseCommitInterrupted)
        {
            await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
                Builders<BroadcastSession>.Update.Set(x => x.IsPermanent, true).Set(x => x.RetentionStartedAtUtc, null));
        }

        var repeated = await service.SetPermanentAsync(session.Id, false);

        Assert.False(repeated.IsPermanent);
        Assert.Equal(cancelled.RetentionStartedAtUtc, repeated.RetentionStartedAtUtc);
        var current = await SessionAsync(fixture, session.Id);
        Assert.False(current.IsPermanent);
        Assert.InRange(current.RetentionStartedAtUtc!.Value,
            cancelled.RetentionStartedAtUtc.Value.AddMilliseconds(-1), cancelled.RetentionStartedAtUtc.Value);
        Assert.Equal(cancelled.RetentionStartedAtUtc, (await ReadManifestAsync(fixture, session)).Session.RetentionStartedAtUtc);
    }

    /// <summary>录制中的旧内存快照不能覆盖随后保存的保留或取消保留标记。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordingProgressPreservesTheLatestPermanentFlag(bool isPermanent)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (stale, segments) = await fixture.SeedSessionAsync();
        stale.IsPermanent = !isPermanent;
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.SetPermanentAsync(stale.Id, !isPermanent);
        var updated = await service.SetPermanentAsync(stale.Id, isPermanent);

        await using (var sink = new RecordingProgressSink(stale, segments, "attempt",
                         Path.Combine(fixture.Root, stale.DirectoryPath), fixture.Mongo, fixture.Publisher,
                         NullLogger.Instance))
        {
            sink.Report(new()
            {
                SegmentId = segments[0].Id,
                RelativePath = segments[0].RelativePath,
                CommittedBytes = segments[0].CommittedBytes,
                DurationMs = segments[0].DurationMs
            });
        }

        Assert.Equal(isPermanent, (await ReadManifestAsync(fixture, stale)).Session.IsPermanent);
        Assert.Equal(isPermanent, (await SessionAsync(fixture, stale.Id)).IsPermanent);
        Assert.Equal(updated.RetentionStartedAtUtc, (await ReadManifestAsync(fixture, stale)).Session.RetentionStartedAtUtc);
        Assert.Equal(updated.RetentionStartedAtUtc?.ToString("yyyyMMddHHmmss"),
            (await SessionAsync(fixture, stale.Id)).RetentionStartedAtUtc?.ToString("yyyyMMddHHmmss"));
    }

    /// <summary>修复发布新媒体版本时保留持久化的保留或取消保留选择。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepairPublicationPreservesPermanentMetadata(bool isPermanent)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.SetPermanentAsync(session.Id, !isPermanent);
        var updated = await service.SetPermanentAsync(session.Id, isPermanent);
        var job = JobTestContext.CreateJob(session, segments, "Repair");

        var (_, execution) = await fixture.ExecuteOwnedAsync(job);
        await execution;

        var current = await SessionAsync(fixture, session.Id);
        Assert.Equal(2, current.MediaRevision);
        Assert.Equal(isPermanent, current.IsPermanent);
        var manifest = await ReadManifestAsync(fixture, session);
        Assert.Equal(2, manifest.Session.MediaRevision);
        Assert.Equal(isPermanent, manifest.Session.IsPermanent);
        Assert.Equal(updated.RetentionStartedAtUtc, manifest.Session.RetentionStartedAtUtc);
        Assert.Equal(updated.RetentionStartedAtUtc?.ToString("yyyyMMddHHmmss"),
            current.RetentionStartedAtUtc?.ToString("yyyyMMddHHmmss"));
    }

    /// <summary>磁盘先保存而数据库尚未提交时，维护遵循持久化保留信息并修复数据库。</summary>
    [Theory]
    [InlineData("permanent")]
    [InlineData("retention-start")]
    public async Task MaintenanceRepairsMetadataFromDurableManifestBeforeDeleting(string pendingChange)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        fixture.Settings.Settings.RecordingRetentionDays = 7;
        session.EndedAtUtc = DateTime.UtcNow.AddDays(-30);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").ReplaceOneAsync(x => x.Id == session.Id, session);
        session.IsPermanent = pendingChange == "permanent";
        session.RetentionStartedAtUtc = pendingChange == "retention-start" ? DateTime.UtcNow : null;
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, session.DirectoryPath, "场次.json"),
            new SessionManifest { Session = session, Segments = segments });
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);

        await service.MaintainAsync();

        var current = await SessionAsync(fixture, session.Id);
        Assert.False(current.IsTrashed);
        Assert.Equal(session.IsPermanent, current.IsPermanent);
        Assert.Equal(session.RetentionStartedAtUtc?.ToString("yyyyMMddHHmmss"),
            current.RetentionStartedAtUtc?.ToString("yyyyMMddHHmmss"));
        Assert.Empty(await fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == session.Id)
            .ToListAsync());
        Assert.True(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));
    }

    /// <summary>回收状态不能通过永久保留接口修改。</summary>
    [Fact]
    public async Task TrashedRecordingRejectsPermanentChanges()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "member");

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.SetPermanentAsync(session.Id, true));

        Assert.Equal(404, error.StatusCode);
        Assert.True((await SessionAsync(fixture, session.Id)).IsTrashed);
    }

    private static Task<BroadcastSession> SessionAsync(JobTestContext fixture, string id) =>
        fixture.Mongo.Collection<BroadcastSession>("sessions").Find(x => x.Id == id).SingleAsync();

    private static async Task<SessionManifest> ReadManifestAsync(JobTestContext fixture, BroadcastSession session)
    {
        await using var input = File.OpenRead(Path.Combine(fixture.Root, session.DirectoryPath, "场次.json"));
        return (await JsonSerializer.DeserializeAsync<SessionManifest>(input, JsonFiles.Options))!;
    }
}
