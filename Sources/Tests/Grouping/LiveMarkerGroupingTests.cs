using System.Text;
using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Grouping.Tests;

/// <summary>验证共享记录在场次投影、操作恢复及回收生命周期中的独立性。</summary>
public sealed class LiveMarkerGroupingTests
{
    /// <summary>拆分保留两侧完整条目，重新归组只合并同源同校准文件。</summary>
    [Fact]
    public async Task SplitAndRegroupKeepAbsoluteTimesAndDeduplicateUnchangedCopies()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var original = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var records = Records(fixture);
        var actor = new LiveMarkerActor("uploader", "原作者", false);
        var uploaded = await UploadAsync(records, original.Session.Id, actor, 125);
        var split = await fixture.Service().SplitAsync(original.Session.Id, original.Segments[1].Id);
        var left = Assert.Single((await records.ListAsync(split[0].Id, actor)).Files);
        var right = Assert.Single((await records.ListAsync(split[1].Id, actor)).Files);
        Assert.NotEqual(uploaded.Id, left.Id);
        Assert.NotEqual(left.Id, right.Id);
        Assert.Equal(actor.Id, right.UploaderId);
        Assert.Equal(1625, Assert.Single((await records.GetAsync(split[0].Id, left.Id, actor)).Entries).OffsetMs);
        Assert.Equal(625, Assert.Single((await records.GetAsync(split[1].Id, right.Id, actor)).Entries).OffsetMs);
        await Assert.ThrowsAsync<InfrastructureException>(() => records.ListAsync(original.Session.Id, actor));
        var regrouped = await fixture.Service().GroupAsync(split[0].Id, [split[1].Id], null);
        var merged = Assert.Single((await records.ListAsync(regrouped.Id, actor)).Files);
        Assert.Equal(1625, Assert.Single((await records.GetAsync(regrouped.Id, merged.Id, actor)).Entries).OffsetMs);
    }

    /// <summary>独立校准不同的副本不可在后续归组中被当成相同文件丢弃。</summary>
    [Fact]
    public async Task IndependentlyAlignedCopiesSurviveRegrouping()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var original = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var records = Records(fixture);
        var actor = new LiveMarkerActor("uploader", "原作者", false);
        await UploadAsync(records, original.Session.Id, actor);
        var split = await fixture.Service().SplitAsync(original.Session.Id, original.Segments[1].Id);
        var right = Assert.Single((await records.ListAsync(split[1].Id, actor)).Files);
        await records.AlignAsync(split[1].Id, right.Id, -250, actor);
        var regrouped = await fixture.Service().GroupAsync(split[0].Id, [split[1].Id], null);
        Assert.Equal(new long[] { -250, 0 },
            (await records.ListAsync(regrouped.Id, actor)).Files.Select(file => file.CalibrationMs).Order().ToArray());
    }

    /// <summary>未完成投影日志冻结附件写入，重复恢复不会重新生成副本或回退校准。</summary>
    [Fact]
    public async Task PendingProjectionFreezesWritesAndRestoresFixedCopyIdentities()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync(spans: [(0, 1000), (1000, 1000)]);
        var records = Records(fixture);
        var actor = new LiveMarkerActor("uploader", "原作者", false);
        var uploaded = await UploadAsync(records, source.Session.Id, actor);
        var left = GroupingFixture.Project(source, false, 1000);
        var right = GroupingFixture.Project(source, true, 1000);
        var files = new LiveMarkerFileStore(fixture.Paths);
        var operation = new ProjectionOperation { Outputs = [left, right], SourceIds = [source.Session.Id] };
        foreach (var output in operation.Outputs)
        {
            operation.LiveMarkerCopies.AddRange(await files.PlanCopiesAsync(source.Session.DirectoryPath,
                output.Session.Id, output.Session.DirectoryPath, new HashSet<string>()));
        }

        var journal = Path.Combine(fixture.Root, ".work", "groups", "markers-pending", "operation.json");
        await JsonFiles.WriteAsync(journal, operation);
        var alignment =
            await Assert.ThrowsAsync<InfrastructureException>(() =>
                records.AlignAsync(source.Session.Id, uploaded.Id, 500, actor));
        Assert.Equal(409, alignment.StatusCode);
        await Assert.ThrowsAsync<InfrastructureException>(() =>
            records.DeleteAsync(source.Session.Id, uploaded.Id, actor));
        await Assert.ThrowsAsync<InfrastructureException>(() => UploadAsync(records, source.Session.Id, actor));
        await Assert.ThrowsAsync<InfrastructureException>(() =>
            new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths).TrashAsync(source.Session.Id,
                actor.Id));
        await files.CompleteCopyAsync(operation.LiveMarkerCopies[0]);
        await fixture.Service().ReconcileAsync();
        Assert.False(File.Exists(journal));
        Assert.Equal(operation.LiveMarkerCopies[0].File.Id,
            Assert.Single((await records.ListAsync(left.Session.Id, actor)).Files).Id);
        Assert.Equal(operation.LiveMarkerCopies[1].File.Id,
            Assert.Single((await records.ListAsync(right.Session.Id, actor)).Files).Id);
        await records.AlignAsync(left.Session.Id, operation.LiveMarkerCopies[0].File.Id, 250, actor);
        await fixture.Service().ReconcileAsync();
        Assert.Equal(250, Assert.Single((await records.ListAsync(left.Session.Id, actor)).Files).CalibrationMs);
    }

    /// <summary>原地保留的场次可完整回收恢复；彻底删除只清附件并保留仍被引用的切片。</summary>
    [Fact]
    public async Task RetainedInPlaceTrashRestoresAttachmentsAndPurgeRemovesTheirMetadata()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync();
        var records = Records(fixture);
        var actor = new LiveMarkerActor("uploader", "原作者", false);
        var uploaded = await UploadAsync(records, source.Session.Id, actor);
        var clip = new Clip
        {
            SessionId = source.Session.Id,
            OwnerId = actor.Id,
            RelativePath = Path.Combine(source.Session.DirectoryPath, "切片", "still-referenced.mp4"),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        };
        var clipPath = Path.Combine(fixture.Root, clip.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);
        await File.WriteAllTextAsync(clipPath, "keep clip");
        await fixture.Mongo.Collection<Clip>("clips").InsertOneAsync(clip);
        var trash = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await trash.TrashAsync(source.Session.Id, actor.Id);
        var entry = await fixture.Mongo.Collection<TrashEntry>("trash").Find(item => item.EntityId == source.Session.Id)
            .SingleAsync();
        Assert.True(entry.RetainedInPlace);
        await Assert.ThrowsAsync<InfrastructureException>(() => records.ListAsync(source.Session.Id, actor));
        await trash.RestoreAsync(entry);
        Assert.Equal(uploaded.Id, Assert.Single((await records.ListAsync(source.Session.Id, actor)).Files).Id);
        await trash.TrashAsync(source.Session.Id, actor.Id);
        entry = await fixture.Mongo.Collection<TrashEntry>("trash").Find(item => item.EntityId == source.Session.Id)
            .SingleAsync();
        await trash.PurgeAsync(entry);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, source.Session.DirectoryPath,
            LiveMarkerFileStore.DIRECTORY_NAME)));
        Assert.Equal("keep clip", await File.ReadAllTextAsync(clipPath));
    }

    /// <summary>普通场次移动进回收站后恢复仍保留文件校准与原文。</summary>
    [Fact]
    public async Task MovedTrashRoundTripKeepsAttachments()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync();
        var records = Records(fixture);
        var actor = new LiveMarkerActor("uploader", "原作者", false);
        var uploaded = await UploadAsync(records, source.Session.Id, actor, -250);
        var trash = new SessionTrashService(fixture.Mongo, fixture.Settings, fixture.Paths);
        await trash.TrashAsync(source.Session.Id, actor.Id);
        var entry = await fixture.Mongo.Collection<TrashEntry>("trash").Find(item => item.EntityId == source.Session.Id)
            .SingleAsync();
        Assert.False(entry.RetainedInPlace);
        Assert.True(File.Exists(Path.Combine(fixture.Root, entry.TrashRelativePath, LiveMarkerFileStore.DIRECTORY_NAME,
            uploaded.Id, LiveMarkerFileStore.ORIGINAL_FILE_NAME)));
        await trash.RestoreAsync(entry);
        Assert.Equal(-250, (await records.GetAsync(source.Session.Id, uploaded.Id, actor)).File.CalibrationMs);
    }

    /// <summary>回收意图落盘但尚未修改场次状态时不能再写附件或创建投影。</summary>
    [Fact]
    public async Task PendingTrashJournalFreezesAttachmentChangesBeforeTheSessionFlagIsSaved()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var source = await fixture.SeedAsync();
        var records = Records(fixture);
        var actor = new LiveMarkerActor("uploader", "原作者", false);
        var uploaded = await UploadAsync(records, source.Session.Id, actor);
        var journal = Path.Combine(fixture.Root, ".trash", "pending-markers-trash", "session-entry.json");
        await JsonFiles.WriteAsync(journal,
            new { entry = new TrashEntry { Kind = "Session", EntityId = source.Session.Id }, manifest = source });
        await Assert.ThrowsAsync<InfrastructureException>(() =>
            records.AlignAsync(source.Session.Id, uploaded.Id, 500, actor));
        await Assert.ThrowsAsync<InfrastructureException>(() =>
            records.DeleteAsync(source.Session.Id, uploaded.Id, actor));
        await Assert.ThrowsAsync<InfrastructureException>(() =>
            fixture.Service().SplitAsync(source.Session.Id, source.Segments[1].Id));
        Assert.Equal(0, (await records.GetAsync(source.Session.Id, uploaded.Id, actor)).File.CalibrationMs);
    }

    private static SessionLiveMarkerService Records(GroupingFixture fixture) => new(fixture.Library, fixture.Settings,
        fixture.Paths, new(fixture.Paths), new(), fixture.Mongo);

    private static async Task<LiveMarkerFileSummary> UploadAsync(SessionLiveMarkerService service, string sessionId,
        LiveMarkerActor actor, long alignment = 0)
    {
        var yaml = Encoding.UTF8.GetBytes(
            "StartTime: 2026-11-12T00:00:00Z\nMarker:\n- RealDateTime: 2026-11-12T00:00:01.500Z\n  LiveTime: 00:00:01.500\n  Message: test\n  MarkerColor: Blue\n");
        using var input = new MemoryStream(yaml);
        return await service.UploadAsync(sessionId, input, "记录.yaml", "UTC", alignment, actor);
    }
}
