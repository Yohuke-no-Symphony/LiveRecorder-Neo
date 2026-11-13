using System.Text.Json;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>真实文件和独立数据库中的录播回收、共享引用与恢复回归。</summary>
public sealed class SessionTrashTests
{
    /// <summary>单独录播实际移入回收站，恢复后过期请求不能再次删除文件。</summary>
    [Fact]
    public async Task MoveRestoreAndStalePurgePreserveRecording()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        var source = Path.Combine(fixture.Root, session.DirectoryPath);
        await service.TrashAsync(session.Id, "admin");
        var entry = await EntryAsync(fixture, session.Id);
        Assert.False(entry.RetainedInPlace);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(fixture.Root, entry.TrashRelativePath, segments[0].RelativePath)));
        Assert.True((await SessionAsync(fixture, session.Id)).IsTrashed);
        await service.RestoreAsync(entry);
        await service.PurgeAsync(entry);
        await service.RestoreAsync(entry);
        await service.MaintainAsync();
        Assert.False((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.Equal("canonical-original-0",
            await File.ReadAllTextAsync(Path.Combine(source, segments[0].RelativePath)));
        Assert.Empty(await fixture.Mongo.Collection<TrashEntry>("trash").Find(_ => true).ToListAsync());
    }

    /// <summary>共享场次的源目录保留到最后一个引用被彻底删除。</summary>
    [Fact]
    public async Task SharedSourceSurvivesPurgeUntilItsLastProjectionIsRemoved()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (source, segments) = await fixture.SeedSessionAsync();
        var projection = await ProjectAsync(fixture, source, segments, "projection");
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(source.Id, "admin");
        var entry = await EntryAsync(fixture, source.Id);
        Assert.True(entry.RetainedInPlace);
        var manifest = JsonSerializer.Deserialize<SessionManifest>(
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, source.DirectoryPath, "场次.json")),
            JsonFiles.Options)!;
        Assert.True(manifest.Session.IsTrashed);
        await service.PurgeAsync(entry);
        var media = Path.Combine(fixture.Root, source.DirectoryPath, segments[0].RelativePath);
        Assert.True(File.Exists(media));
        Assert.False(File.Exists(Path.Combine(fixture.Root, source.DirectoryPath, "场次.json")));
        await service.TrashAsync(projection.Id, "admin");
        await service.PurgeAsync(await EntryAsync(fixture, projection.Id));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, source.DirectoryPath)));
    }

    /// <summary>拆分的一个场次清理后，另一个仍可使用共享源；全部清理后释放隐藏来源。</summary>
    [Fact]
    public async Task SplitProjectionsReleaseHiddenOriginalOnlyAfterBothArePurged()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (source, segments) = await fixture.SeedSessionAsync();
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == source.Id,
            Builders<BroadcastSession>.Update.Set(x => x.IsHidden, true));
        var left = await ProjectAsync(fixture, source, segments, "left", true);
        var right = await ProjectAsync(fixture, source, segments, "right", true);
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(left.Id, "admin");
        await service.PurgeAsync(await EntryAsync(fixture, left.Id));
        Assert.True(File.Exists(Path.Combine(fixture.Root, source.DirectoryPath, segments[0].RelativePath)));
        Assert.NotNull(await SessionAsync(fixture, source.Id));
        await service.TrashAsync(right.Id, "admin");
        await service.PurgeAsync(await EntryAsync(fixture, right.Id));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, source.DirectoryPath)));
        Assert.Equal(0, await fixture.Mongo.Collection<BroadcastSession>("sessions").CountDocumentsAsync(_ => true));
    }

    /// <summary>进行中的录制、开放片段与媒体作业禁止删除。</summary>
    [Theory]
    [InlineData("recording")]
    [InlineData("segment")]
    [InlineData("job")]
    public async Task ActiveRecordingAndMediaJobsCannotBeTrashed(string reason)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        if (reason == "recording")
        {
            await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
                Builders<BroadcastSession>.Update.Set(x => x.EndedAtUtc, null));
        }
        else if (reason == "segment")
        {
            await fixture.Mongo.Collection<RecordingSegment>("segments").UpdateOneAsync(x => x.Id == segments[0].Id,
                Builders<RecordingSegment>.Update.Set(x => x.IsClosed, false));
        }
        else
        {
            await fixture.Mongo.Collection<MediaJob>("jobs")
                .InsertOneAsync(JobTestContext.CreateJob(session, segments));
        }

        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.TrashAsync(session.Id, "admin"));
        Assert.Equal(409, error.StatusCode);
        Assert.False((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, session.DirectoryPath)));
    }

    /// <summary>恢复文件移动之后崩溃时，维护从日志完成恢复且不会重新回收。</summary>
    [Fact]
    public async Task MaintenanceResumesRestoreAfterDirectoryWasMoved()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "admin");
        var entry = await EntryAsync(fixture, session.Id);
        var journalPath = Path.Combine(fixture.Root, ".trash", entry.Id, "session-entry.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!;
        json["entry"]!["state"] = "Restoring";
        await File.WriteAllTextAsync(journalPath, json.ToJsonString());
        Directory.Move(Path.Combine(fixture.Root, entry.TrashRelativePath),
            Path.Combine(fixture.Root, entry.OriginalRelativePath));
        await service.MaintainAsync();
        Assert.False((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.False(File.Exists(journalPath));
        await service.MaintainAsync();
        Assert.False((await SessionAsync(fixture, session.Id)).IsTrashed);
    }

    /// <summary>回收日志可以重新建立丢失的数据库索引，随后到期清理实际文件。</summary>
    [Fact]
    public async Task DurableJournalRebuildsTrashStateAndExpiresRecording()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "admin");
        var entry = await EntryAsync(fixture, session.Id);
        await fixture.Mongo.Collection<TrashEntry>("trash").DeleteManyAsync(_ => true);
        await fixture.Mongo.Collection<BroadcastSession>("sessions").DeleteManyAsync(_ => true);
        await service.MaintainAsync();
        Assert.True((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.Equal(entry.Id, (await EntryAsync(fixture, session.Id)).Id);
        var journalPath = Path.Combine(fixture.Root, ".trash", entry.Id, "session-entry.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!;
        json["entry"]!["createdAtUtc"] = DateTime.UtcNow.AddDays(-8);
        await File.WriteAllTextAsync(journalPath, json.ToJsonString());
        await service.MaintainAsync();
        Assert.Equal(0, await fixture.Mongo.Collection<BroadcastSession>("sessions").CountDocumentsAsync(_ => true));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, entry.TrashRelativePath)));
    }

    /// <summary>有效切片独立于源录播保留，到切片移入回收站后才释放源目录。</summary>
    [Fact]
    public async Task ValidClipSurvivesSessionPurgeAndItsOwnRestore()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var clip = new Clip
        {
            SessionId = session.Id, OwnerId = "another-viewer", ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
            RelativePath = Path.Combine(session.DirectoryPath, "切片", "clip.mp4"), Bytes = 8
        };
        var clipPath = Path.Combine(fixture.Root, clip.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);
        await File.WriteAllTextAsync(clipPath, "playable");
        await fixture.Mongo.Collection<Clip>("clips").InsertOneAsync(clip);
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "admin");
        var entry = await EntryAsync(fixture, session.Id);
        Assert.True(entry.RetainedInPlace);
        await service.PurgeAsync(entry);
        Assert.Equal("playable", await File.ReadAllTextAsync(clipPath));
        await fixture.Storage.TrashClipAsync(clip);
        await service.MaintainAsync();
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, session.DirectoryPath)));
        await fixture.Storage.RestoreAsync(await EntryAsync(fixture, clip.Id));
        Assert.Equal("playable", await File.ReadAllTextAsync(clipPath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, "场次.json")));
    }

    /// <summary>首次删除仅写完磁盘日志时，重试沿用同一意图，不留下恢复后会重删的孤立日志。</summary>
    [Fact]
    public async Task RetryAdoptsJournalWrittenBeforeDatabaseInsert()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "admin");
        var original = await EntryAsync(fixture, session.Id);
        var journalPath = Path.Combine(fixture.Root, ".trash", original.Id, "session-entry.json");
        var pending = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!;
        pending["entry"]!["state"] = "Pending";
        await service.RestoreAsync(original);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await File.WriteAllTextAsync(journalPath, pending.ToJsonString());
        await service.TrashAsync(session.Id, "admin");
        var adopted = await EntryAsync(fixture, session.Id);
        Assert.Equal(original.Id, adopted.Id);
        await service.RestoreAsync(adopted);
        await service.MaintainAsync();
        Assert.False((await SessionAsync(fixture, session.Id)).IsTrashed);
        Assert.True(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));
    }

    /// <summary>索引重建尚未导入投影时，正式磁盘清单仍保护共享源文件。</summary>
    [Fact]
    public async Task DiskProjectionProtectsRetiredSourceBeforeDatabaseRebuild()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (source, segments) = await fixture.SeedSessionAsync();
        await ProjectAsync(fixture, source, segments, "disk-projection");
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(source.Id, "admin");
        await service.PurgeAsync(await EntryAsync(fixture, source.Id));
        await fixture.Mongo.Collection<BroadcastSession>("sessions").DeleteManyAsync(_ => true);
        await fixture.Mongo.Collection<RecordingSegment>("segments").DeleteManyAsync(_ => true);
        await service.MaintainAsync();
        Assert.True(File.Exists(Path.Combine(fixture.Root, source.DirectoryPath, segments[0].RelativePath)));
    }

    /// <summary>过期但尚未回收的切片，以及中断在文件移动前的切片意图，仍保护可恢复文件。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrInterruptedClipRemainsRecoverableAfterSessionPurge(bool interrupted)
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, _) = await fixture.SeedSessionAsync();
        var clip = new Clip
        {
            OwnerId = "viewer", SessionId = session.Id, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
            RelativePath = Path.Combine(session.DirectoryPath, "切片", "expired.mp4"), Bytes = 8
        };
        var path = Path.Combine(fixture.Root, clip.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "clip-data");
        await fixture.Mongo.Collection<Clip>("clips").InsertOneAsync(clip);
        if (interrupted)
        {
            await fixture.Storage.TrashClipAsync(clip);
            var moving = await EntryAsync(fixture, clip.Id);
            File.Move(Path.Combine(fixture.Root, moving.TrashRelativePath), path);
            await fixture.Mongo.Collection<TrashEntry>("trash").UpdateOneAsync(x => x.Id == moving.Id,
                Builders<TrashEntry>.Update.Set(x => x.State, "Moving"));
        }

        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "admin");
        Assert.True((await EntryAsync(fixture, session.Id)).RetainedInPlace);
        await service.PurgeAsync(await EntryAsync(fixture, session.Id));
        Assert.Equal("clip-data", await File.ReadAllTextAsync(path));
        await fixture.Storage.MaintainAsync();
        await service.MaintainAsync();
        await fixture.Storage.RestoreAsync(await EntryAsync(fixture, clip.Id));
        Assert.Equal("clip-data", await File.ReadAllTextAsync(path));
    }

    /// <summary>Pending 日志恢复先检查新增作业，随后使用最新媒体版本而非旧快照。</summary>
    [Fact]
    public async Task PendingIntentChecksActiveJobAndRefreshesRepairedRevision()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "admin");
        var entry = await EntryAsync(fixture, session.Id);
        var journalPath = Path.Combine(fixture.Root, ".trash", entry.Id, "session-entry.json");
        var pending = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!;
        pending["entry"]!["state"] = "Pending";
        await service.RestoreAsync(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await File.WriteAllTextAsync(journalPath, pending.ToJsonString());
        var job = JobTestContext.CreateJob(session, segments, "Repair");
        await fixture.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.TrashAsync(session.Id, "admin"));
        Assert.Equal("LIBRARY_DELETE_JOB_ACTIVE", error.Code);
        Assert.True(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));
        await fixture.Mongo.Collection<MediaJob>("jobs").UpdateOneAsync(x => x.Id == job.Id,
            Builders<MediaJob>.Update.Set(x => x.State, "Completed"));
        await fixture.Mongo.Collection<BroadcastSession>("sessions").UpdateOneAsync(x => x.Id == session.Id,
            Builders<BroadcastSession>.Update.Set(x => x.MediaRevision, 2));
        var repaired = new RecordingSegment
        {
            SessionId = session.Id, RelativePath = "source/repaired.flv", Revision = 2, IsClosed = true,
            DurationMs = 1000, CommittedBytes = 8
        };
        await fixture.Mongo.Collection<RecordingSegment>("segments").InsertOneAsync(repaired);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, session.DirectoryPath, repaired.RelativePath),
            "repaired");
        await service.TrashAsync(session.Id, "admin");
        Assert.Equal(2, (await SessionAsync(fixture, session.Id)).MediaRevision);
        await service.RestoreAsync(await EntryAsync(fixture, session.Id));
        Assert.Equal(2, (await SessionAsync(fixture, session.Id)).MediaRevision);
        Assert.Equal("repaired",
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, session.DirectoryPath, repaired.RelativePath)));
    }

    /// <summary>完成的派生输出独立保留；仅释放清单内原片和索引，目录中的未知文件不参与清理。</summary>
    [Fact]
    public async Task CompletedOutputRetainsItsFileWithoutRetainingKnownOriginalMedia()
    {
        await using var fixture = await JobTestContext.CreateAsync();
        var (session, segments) = await fixture.SeedSessionAsync();
        var output = Path.Combine(session.DirectoryPath, "派生", "merged.flv");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Root, output))!);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, output), "merged-output");
        var unknown = Path.Combine(fixture.Root, session.DirectoryPath, "user-note.txt");
        await File.WriteAllTextAsync(unknown, "user-owned");
        var job = JobTestContext.CreateJob(session, segments, "Merge");
        job.State = "Completed";
        job.OutputRelativePaths = [output];
        await fixture.Mongo.Collection<MediaJob>("jobs").InsertOneAsync(job);
        var service = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await service.TrashAsync(session.Id, "admin");
        await service.PurgeAsync(await EntryAsync(fixture, session.Id));
        Assert.False(File.Exists(Path.Combine(fixture.Root, session.DirectoryPath, segments[0].RelativePath)));
        Assert.Equal("merged-output", await File.ReadAllTextAsync(Path.Combine(fixture.Root, output)));
        Assert.Equal("user-owned", await File.ReadAllTextAsync(unknown));
    }

    /// <summary>正式目录重建不会导入回收站和未发布的工作目录。</summary>
    [Fact]
    public void ReconcileExcludesTrashAndWorkManifests()
    {
        Assert.False(SessionTrashService.CanReconcile("/records", "/records/.trash/id/recording/场次.json"));
        Assert.False(SessionTrashService.CanReconcile("/records", "/records/.work/id/场次.json"));
        Assert.True(SessionTrashService.CanReconcile("/records", "/records/主播/场次/场次.json"));
    }

    private static Task<TrashEntry> EntryAsync(JobTestContext fixture, string id) =>
        fixture.Mongo.Collection<TrashEntry>("trash").Find(x => x.EntityId == id).SingleAsync();

    private static Task<BroadcastSession> SessionAsync(JobTestContext fixture, string id) =>
        fixture.Mongo.Collection<BroadcastSession>("sessions").Find(x => x.Id == id).SingleAsync();

    private static async Task<BroadcastSession> ProjectAsync(JobTestContext fixture, BroadcastSession source,
        List<RecordingSegment> segments, string name, bool provenance = false)
    {
        var projection = new BroadcastSession
        {
            DirectoryPath = "主播_1/" + name, Title = name, StartedAtUtc = source.StartedAtUtc,
            EndedAtUtc = source.EndedAtUtc,
            State = "Completed", HasDanmu = true, GroupedSessionIds = provenance ? [source.Id] : [],
            EventSources = [new() { DirectoryPath = source.DirectoryPath, FromMs = 0 }]
        };
        await fixture.Mongo.Collection<BroadcastSession>("sessions").InsertOneAsync(projection);
        foreach (var segment in segments)
        {
            await fixture.Mongo.Collection<RecordingSegment>("segments").InsertOneAsync(new()
            {
                SessionId = projection.Id, RelativePath = segment.RelativePath,
                RootRelativePath = Path.Combine(source.DirectoryPath, segment.RelativePath),
                DurationMs = segment.DurationMs, CommittedBytes = segment.CommittedBytes, IsClosed = true
            });
        }

        var projectedSegments = await fixture.Mongo.Collection<RecordingSegment>("segments")
            .Find(x => x.SessionId == projection.Id).ToListAsync();
        await JsonFiles.WriteAsync(Path.Combine(fixture.Root, projection.DirectoryPath, "场次.json"),
            new SessionManifest { Session = projection, Segments = projectedSegments });
        return projection;
    }
}
