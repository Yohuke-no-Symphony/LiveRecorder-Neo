using Xunit;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证真实文件分类、重叠目录边界与短期扫描快照。</summary>
public sealed class StorageUsageTests
{
    /// <summary>嵌套实例目录只扫描一次，各类实际文件互不重复计数。</summary>
    [Fact]
    public async Task SeparatesRecordingClipTrashWorkAndCacheWithoutDoubleCountingNestedRecords()
    {
        using var fixture = new StorageUsageTestContext();
        var records = fixture.Paths.RecordsDirectory;
        await fixture.WriteAsync(records, "room/session/source.flv", 11);
        await fixture.WriteAsync(records, "room/session/events.jsonl", 3);
        await fixture.WriteAsync(records, "room/session/切片/owner/job/v1/output.mp4", 7);
        await fixture.WriteAsync(records, ".trash/entry/payload.mp4", 13);
        await fixture.WriteAsync(records, ".work/jobs/pending/output.mp4", 17);
        await fixture.WriteAsync(fixture.Paths.CacheDirectory, "playback/window.ts", 19);
        await fixture.WriteAsync(fixture.Paths.KeysDirectory, "key.xml", 23);

        var report = await fixture.Service.GetDetailsAsync();

        Assert.False(report.IsPartial);
        var recordingVolume = Assert.Single(report.Volumes, volume => volume.Id == "recordings");
        Assert.Equal(14L, recordingVolume.RecordingBytes);
        Assert.Equal(7L, recordingVolume.ClipBytes);
        Assert.Equal(13L, recordingVolume.TrashBytes);
        Assert.Equal(0L, recordingVolume.CacheBytes);
        Assert.Equal(recordingVolume.UsedBytes,
            recordingVolume.RecordingBytes + recordingVolume.ClipBytes + recordingVolume.OtherBytes);
        var dataVolume = Assert.Single(report.Volumes, volume => volume.Id == "data");
        Assert.Equal(19L, dataVolume.CacheBytes);
        Assert.Equal(0L, dataVolume.RecordingBytes);
        Assert.Equal(0L, dataVolume.ClipBytes);
        Assert.Equal(93L, report.Directories.Sum(directory => directory.TotalBytes));
        Assert.Equal(21L, Assert.Single(report.Rooms).TotalBytes);
        Assert.Equal(7L, Assert.Single(report.Users).ClipBytes);
        Assert.Equal(17L, Assert.Single(report.Directories, directory => directory.Kind == "work").OtherBytes);
    }

    /// <summary>未发布版本和旧版本的切片仍属于实际个人空间占用。</summary>
    [Fact]
    public async Task CountsEveryPhysicalClipVersionAndAggregatesOwnersAcrossRooms()
    {
        using var fixture = new StorageUsageTestContext();
        var records = fixture.Paths.RecordsDirectory;
        await fixture.WriteAsync(records, "room_one/session/切片/owner/job/v1/output.mp4", 5);
        await fixture.WriteAsync(records, "room_one/session/切片/owner/job/v2/output.mp4", 7);
        await fixture.WriteAsync(records, "room_two/session/切片/owner/pending/v1/partial.mp4", 11);
        await fixture.WriteAsync(records, "room_two/session/切片/other/job/v1/output.mp4", 13);

        var report = await fixture.Service.GetDetailsAsync();

        Assert.Equal(36L, report.Volumes[0].ClipBytes);
        Assert.Equal(0L, report.Volumes[0].RecordingBytes);
        Assert.Equal(23L, Assert.Single(report.Users, user => user.OwnerId == "owner").ClipBytes);
        Assert.Equal(13L, Assert.Single(report.Users, user => user.OwnerId == "other").ClipBytes);
        Assert.Equal(36L, report.Rooms.Sum(room => room.ClipBytes));
        Assert.Equal(3, report.Directories.Count(directory => directory.Kind == "clips"));
    }

    /// <summary>移动到回收站的切片路径不再同时计入个人切片类别。</summary>
    [Fact]
    public async Task TrashTakesPrecedenceOverNestedClipDirectoryNames()
    {
        using var fixture = new StorageUsageTestContext();
        await fixture.WriteAsync(fixture.Paths.RecordsDirectory, ".trash/entry/recording/切片/owner/job/v1/output.mp4", 17);

        var report = await fixture.Service.GetDetailsAsync();

        Assert.Equal(17L, report.Volumes[0].TrashBytes);
        Assert.Equal(0L, report.Volumes[0].RecordingBytes);
        Assert.Equal(0L, report.Volumes[0].ClipBytes);
        Assert.Empty(report.Rooms);
        Assert.Empty(report.Users);
        Assert.Equal(17L, Assert.Single(report.Directories).TrashBytes);
    }

    /// <summary>缓存复用与显式刷新共享同一份新的摘要数据。</summary>
    [Fact]
    public async Task ReusesSnapshotUntilRefreshExpiryOrInvalidation()
    {
        using var fixture = new StorageUsageTestContext();
        var records = fixture.Paths.RecordsDirectory;
        await fixture.WriteAsync(records, "room/session/source.flv", 5);
        var first = await fixture.Service.GetDetailsAsync();
        await fixture.WriteAsync(records, "room/session/source.flv", 11);

        Assert.Same(first, await fixture.Service.GetDetailsAsync());
        var refreshed = await fixture.Service.GetDetailsAsync(true);
        Assert.NotSame(first, refreshed);
        Assert.Equal(11L, (await fixture.Service.GetVolumesAsync())[0].RecordingBytes);

        await fixture.WriteAsync(records, "room/session/source.flv", 17);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(17L, (await fixture.Service.GetDetailsAsync()).Volumes[0].RecordingBytes);
        await fixture.WriteAsync(records, "room/session/source.flv", 23);
        fixture.Service.Invalidate();
        Assert.Equal(23L, (await fixture.Service.GetDetailsAsync()).Volumes[0].RecordingBytes);
    }

    /// <summary>预先取消的请求不返回缓存，也不阻止后续完整扫描。</summary>
    [Fact]
    public async Task CancelledRequestDoesNotPublishOrPoisonSnapshot()
    {
        using var fixture = new StorageUsageTestContext();
        await fixture.WriteAsync(fixture.Paths.RecordsDirectory, "room/session/source.flv", 11);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetDetailsAsync(cancellationToken: cancellation.Token));
        Assert.Equal(11L, (await fixture.Service.GetDetailsAsync()).Volumes[0].RecordingBytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetDetailsAsync(cancellationToken: cancellation.Token));
    }

    /// <summary>独立的数据根与录像根各自扫描，不把路径前缀相似的兄弟目录排除。</summary>
    [Fact]
    public async Task ScansDisjointRootsAndPrefixSharingSiblingDirectories()
    {
        using var fixture = new StorageUsageTestContext(false);
        await fixture.WriteAsync(fixture.Paths.RecordsDirectory, "room/session/source.flv", 11);
        await fixture.WriteAsync(fixture.Paths.DataDirectory, "records-backup/file.bin", 17);
        await fixture.WriteAsync(fixture.Paths.CacheDirectory, "playback/window.ts", 23);

        var report = await fixture.Service.GetDetailsAsync();

        Assert.False(report.IsPartial);
        Assert.Equal(11L, report.Volumes[0].RecordingBytes);
        Assert.Equal(23L, report.Volumes[1].CacheBytes);
        Assert.Equal(51L, report.Directories.Sum(directory => directory.TotalBytes));
    }

    /// <summary>即便收到无效的相等根配置，也只扫描一次并明确报告分类不完整。</summary>
    [Fact]
    public async Task EqualRootsDoNotCountTheSameFilesTwice()
    {
        using var fixture = new StorageUsageTestContext();
        var paths = new AppPaths(fixture.Paths.DataDirectory, fixture.Paths.DataDirectory);
        await fixture.WriteAsync(paths.RecordsDirectory, "room/session/source.flv", 11);
        var service = new StorageUsageService(paths, fixture.Clock);

        var report = await service.GetDetailsAsync();

        Assert.True(report.IsPartial);
        Assert.Equal(11L, report.Volumes[0].RecordingBytes);
        Assert.Equal(11L, report.Directories.Sum(directory => directory.TotalBytes));
    }

    /// <summary>文件链接与目录循环不泄漏根外文件，也不造成重复或无限遍历。</summary>
    [Fact]
    public async Task IgnoresSymbolicFileAndDirectoryLinks()
    {
        using var fixture = new StorageUsageTestContext();
        var records = fixture.Paths.RecordsDirectory;
        await fixture.WriteAsync(records, "room/session/source.flv", 5);
        await fixture.WriteAsync(fixture.Paths.DataDirectory, "outside/secret.flv", 97);
        try
        {
            File.CreateSymbolicLink(Path.Combine(records, "room", "linked.flv"), Path.Combine(fixture.Paths.DataDirectory, "outside", "secret.flv"));
            Directory.CreateSymbolicLink(Path.Combine(records, "room", "loop"), records);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }

        var report = await fixture.Service.GetDetailsAsync();

        Assert.Equal(5L, report.Volumes[0].RecordingBytes);
        Assert.Equal(102L, report.Directories.Sum(directory => directory.TotalBytes));
    }
}
