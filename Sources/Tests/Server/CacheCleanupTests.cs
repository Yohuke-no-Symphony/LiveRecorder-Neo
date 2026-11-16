using Xunit;
using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证缓存清理的类别边界、活跃保护、访问时间及目录安全。</summary>
public sealed class CacheCleanupTests
{
    /// <summary>仅删除所选播放缓存并使空间快照失效，持久化数据与其他缓存保持完整。</summary>
    [Fact]
    public async Task CleansSelectedCategoryAndInvalidatesUsageWithoutDeletingPersistentData()
    {
        using var fixture = new CacheCleanupTestContext();
        await fixture.WriteAsync("playback/old/generation/chunk.m4s", 19);
        await fixture.WriteAsync("artwork/" + new string('a', 64) + ".image", 23);
        await fixture.WriteAsync("analytics-contributions/" + new string('b', 64) + ".json", 29);
        await fixture.WriteAsync("cover-frames/task/frame.jpg", 31);
        await fixture.WriteAsync("unknown/keep.bin", 37);
        foreach (var relative in new[] { "room/source.flv", ".derived/output.mp4", "room/切片/owner/output.mp4", ".trash/payload.mp4", "room/分析/ai.json" })
        {
            await fixture.Usage.WriteAsync(fixture.Paths.RecordsDirectory, relative, 41);
        }
        var before = await fixture.Usage.Service.GetDetailsAsync();

        var result = await fixture.Service.CleanupAsync(["playback"], 0);
        var after = await fixture.Usage.Service.GetDetailsAsync();

        Assert.Equal(19, result.DeletedBytes);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal("playback", Assert.Single(result.Categories).Category);
        Assert.NotSame(before, after);
        Assert.Equal(before.Volumes[1].CacheBytes - 19, after.Volumes[1].CacheBytes);
        Assert.False(File.Exists(fixture.CachePath("playback/old/generation/chunk.m4s")));
        Assert.True(File.Exists(fixture.CachePath("artwork/" + new string('a', 64) + ".image")));
        Assert.True(File.Exists(fixture.CachePath("analytics-contributions/" + new string('b', 64) + ".json")));
        Assert.True(File.Exists(fixture.CachePath("cover-frames/task/frame.jpg")));
        Assert.True(File.Exists(fixture.CachePath("unknown/keep.bin")));
        Assert.Equal(5, Directory.GetFiles(fixture.Paths.RecordsDirectory, "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>手动清理仍保留生成中的整个键及最近请求过的播放代次。</summary>
    [Fact]
    public async Task KeepsBuildingAndRecentlyUsedPlaybackWhileDeletingInactiveGenerations()
    {
        using var fixture = new CacheCleanupTestContext();
        await fixture.WriteAsync("playback/building/generation/chunk.m4s", 11);
        await fixture.WriteAsync("playback/recent/generation/chunk.m4s", 13);
        await fixture.WriteAsync("playback/old/generation/chunk.m4s", 17);
        using var lease = fixture.Playback.ProtectBuild("building", "different-generation");
        fixture.Playback.Touch("recent", "generation");

        var result = await fixture.Service.CleanupAsync(["playback"], 0);

        Assert.Equal(17, result.DeletedBytes);
        Assert.Equal(2, result.SkippedEntries);
        Assert.True(File.Exists(fixture.CachePath("playback/building/generation/chunk.m4s")));
        Assert.True(File.Exists(fixture.CachePath("playback/recent/generation/chunk.m4s")));
        Assert.False(File.Exists(fixture.CachePath("playback/old/generation/chunk.m4s")));
    }

    /// <summary>现有自动维护仍按保留期限或容量回收，并使用相同的活跃与生成保护。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticMaintenancePreservesExistingRetentionAndCapacityPolicy(bool capacityExceeded)
    {
        using var fixture = new CacheCleanupTestContext();
        fixture.Settings.Settings.CacheLimitBytes = capacityExceeded ? 0 : 4096;
        fixture.Settings.Settings.CacheRetentionMinutes = 60;
        await fixture.WriteAsync("playback/old/generation/chunk.m4s", 11);
        await fixture.WriteAsync("playback/new/generation/chunk.m4s", 13);
        await fixture.WriteAsync("playback/recent/generation/chunk.m4s", 17);
        await fixture.WriteAsync("playback/building/generation/chunk.m4s", 19);
        var oldFile = fixture.CachePath("playback/old/generation/chunk.m4s");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddHours(-2));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(oldFile)!, DateTime.UtcNow.AddHours(-2));
        fixture.Playback.Touch("recent", "generation");
        using var lease = fixture.Playback.ProtectBuild("building", "generation");

        await fixture.Playback.MaintainAsync();

        Assert.False(File.Exists(oldFile));
        Assert.Equal(!capacityExceeded, File.Exists(fixture.CachePath("playback/new/generation/chunk.m4s")));
        Assert.True(File.Exists(fixture.CachePath("playback/recent/generation/chunk.m4s")));
        Assert.True(File.Exists(fixture.CachePath("playback/building/generation/chunk.m4s")));
    }

    /// <summary>最近访问标记跨缓存实例保留，进程重启不会让旧文件被当作长期未使用。</summary>
    [Fact]
    public async Task PlaybackUsageTimeSurvivesCacheRecreationWithoutChangingMediaTimestamp()
    {
        using var fixture = new CacheCleanupTestContext();
        await fixture.WriteAsync("playback/old/generation/chunk.m4s", 11);
        var path = fixture.CachePath("playback/old/generation/chunk.m4s");
        var modified = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(path, modified);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, modified);
        fixture.Playback.Touch("old", "generation");
        using var recreated = new PlaybackCache(fixture.Paths, fixture.Settings);

        var result = await recreated.CleanupAsync(60);

        Assert.Equal(0, result.DeletedBytes);
        Assert.Equal(1, result.SkippedEntries);
        Assert.True(File.Exists(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>清理过程中失效的旧扫描不能重新发布已删除文件的占用。</summary>
    [Fact]
    public async Task InvalidationDuringScanDoesNotRepublishDeletedCacheFiles()
    {
        using var fixture = new CacheCleanupTestContext();
        await fixture.WriteAsync("playback/old/generation/chunk.m4s", 11);
        var clock = new StorageUsageBlockingTimeProvider();
        var usage = new StorageUsageService(fixture.Paths, clock);
        var pending = usage.GetDetailsAsync();
        try
        {
            await clock.SnapshotCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.Delete(fixture.CachePath("playback/old/generation/chunk.m4s"));
            usage.Invalidate();
            clock.Resume.TrySetResult();

            var report = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, report.Volumes[1].CacheBytes);
            Assert.Same(report, await usage.GetDetailsAsync());
        }
        finally
        {
            clock.Resume.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>未达到最短未使用时间的文件保留，重复清理不重复报告已释放空间。</summary>
    [Fact]
    public async Task AppliesMinimumUnusedTimeAndRepeatedCleanupIsIdempotent()
    {
        using var fixture = new CacheCleanupTestContext();
        var old = "analytics-contributions/" + new string('a', 64) + ".json";
        var recent = "analytics-contributions/" + new string('b', 64) + ".json";
        await fixture.WriteAsync(old, 11);
        await fixture.WriteAsync(recent, 13);
        File.SetLastWriteTimeUtc(fixture.CachePath(old), DateTime.UtcNow.AddHours(-2));

        var first = await fixture.Service.CleanupAsync(["analyticsContributions"], 60);
        var repeated = await fixture.Service.CleanupAsync(["analyticsContributions"], 60);

        Assert.Equal(11, first.DeletedBytes);
        Assert.Equal(1, first.SkippedEntries);
        Assert.Equal(0, repeated.DeletedBytes);
        Assert.True(File.Exists(fixture.CachePath(recent)));
    }

    /// <summary>统计内存命中刷新使用时间，清理磁盘快照后仍能从原始事件重建相同结果。</summary>
    [Fact]
    public async Task ContributionCacheTracksMemoryHitsAndRebuildsFromPreservedEvents()
    {
        using var fixture = new CacheCleanupTestContext();
        var detail = new SessionDetail
        {
            Session = new() { Id = "session", DirectoryPath = "room/session", State = "Completed" },
            DurationMs = 10000
        };
        var archive = Path.Combine(fixture.Paths.RecordsDirectory, "room", "session", "弹幕", "事件.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var item = new RecordedEventEnvelope
        {
            Sequence = 1, Kind = "SuperChat", OffsetMs = 1000, UserId = "42", UserName = "viewer", Amount = 30
        };
        var original = JsonSerializer.Serialize(item, JsonFiles.Options) + "\n";
        await File.WriteAllTextAsync(archive, original);
        var first = await fixture.Contributions.GetAsync(detail, fixture.Paths.RecordsDirectory, "SuperChat");
        var snapshot = fixture.CachePath("analytics-contributions/" + first.SnapshotId + ".json");
        File.SetLastWriteTimeUtc(snapshot, DateTime.UtcNow.AddHours(-2));

        var cached = await fixture.Contributions.GetAsync(detail, fixture.Paths.RecordsDirectory, "SuperChat");
        var retained = await fixture.Service.CleanupAsync(["analyticsContributions"], 60);

        Assert.Equal(first.SnapshotId, cached.SnapshotId);
        Assert.Equal(0, retained.DeletedFiles);
        Assert.Equal(1, retained.SkippedEntries);
        Assert.True(File.Exists(snapshot));
        Assert.Equal(1, (await fixture.Service.CleanupAsync(["analyticsContributions"], 0)).DeletedFiles);
        Assert.False(File.Exists(snapshot));
        using var reader = new EventArchiveReader();
        using var recreated = new AnalyticsContributionService(reader, fixture.Paths);
        var rebuilt = await recreated.GetAsync(detail, fixture.Paths.RecordsDirectory, "SuperChat");

        Assert.Equal(first.SnapshotId, rebuilt.SnapshotId);
        Assert.Equal(first.Summary.Amount, rebuilt.Summary.Amount);
        Assert.Equal(30, rebuilt.Summary.Amount);
        Assert.Single(rebuilt.Items);
        Assert.True(File.Exists(snapshot));
        Assert.Equal(original, await File.ReadAllTextAsync(archive));
    }

    /// <summary>目录与文件符号链接不会让清理进入缓存外的持久化数据。</summary>
    [Fact]
    public async Task DoesNotFollowLinkedCategoryGenerationOrFile()
    {
        using var fixture = new CacheCleanupTestContext();
        var outside = Path.Combine(fixture.Paths.RecordsDirectory, "protected");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "keep.flv"), new byte[97]);
        await fixture.WriteAsync("playback/safe/generation/chunk.m4s", 11);
        try
        {
            Directory.CreateSymbolicLink(fixture.CachePath("playback/linked"), outside);
            File.CreateSymbolicLink(fixture.CachePath("playback/safe/generation/link.flv"), Path.Combine(outside, "keep.flv"));
            Directory.CreateSymbolicLink(fixture.CachePath("analytics-contributions"), outside);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await fixture.Service.CleanupAsync(["playback", "analyticsContributions"], 0);

        Assert.Equal(11, result.DeletedBytes);
        Assert.True(result.SkippedEntries >= 3);
        Assert.Equal(97, new FileInfo(Path.Combine(outside, "keep.flv")).Length);
        Assert.True(Directory.Exists(fixture.CachePath("playback/linked")));
    }

    /// <summary>缓存根自身为链接时保留目标文件。</summary>
    [Fact]
    public async Task DoesNotFollowLinkedCacheRoot()
    {
        using var fixture = new CacheCleanupTestContext();
        var outside = Path.Combine(fixture.Paths.RecordsDirectory, "protected");
        Directory.CreateDirectory(Path.Combine(outside, "playback", "key", "generation"));
        var protectedFile = Path.Combine(outside, "playback", "key", "generation", "keep.flv");
        await File.WriteAllBytesAsync(protectedFile, new byte[97]);
        Directory.Delete(fixture.Paths.CacheDirectory);
        try
        {
            Directory.CreateSymbolicLink(fixture.Paths.CacheDirectory, outside);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await fixture.Service.CleanupAsync(["playback"], 0);

        Assert.Equal(0, result.DeletedBytes);
        Assert.True(result.SkippedEntries > 0);
        Assert.Equal(97, new FileInfo(protectedFile).Length);
    }

    /// <summary>客户端类别不能指定任意路径，非法选项不会开始删除。</summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("../records", 0)]
    [InlineData("playback", -1)]
    [InlineData("playback", 525601)]
    public async Task RejectsInvalidOptionsBeforeDeletingFiles(string? category, int minimumUnusedMinutes)
    {
        using var fixture = new CacheCleanupTestContext();
        await fixture.WriteAsync("playback/old/generation/chunk.m4s", 11);

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Service.CleanupAsync(
            category is null ? null : category.Length == 0 ? [] : [category], minimumUnusedMinutes));

        Assert.Equal(MessageCode.REQUEST_INVALID.ToString(), error.Code);
        Assert.True(File.Exists(fixture.CachePath("playback/old/generation/chunk.m4s")));
    }

    /// <summary>已取消的请求不删除文件，后续清理仍能继续使用同步资源。</summary>
    [Fact]
    public async Task CancellationDoesNotDeleteFilesOrPoisonSubsequentCleanup()
    {
        using var fixture = new CacheCleanupTestContext();
        await fixture.WriteAsync("playback/old/generation/chunk.m4s", 11);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CleanupAsync(["playback"], 0, cancellation.Token));
        Assert.True(File.Exists(fixture.CachePath("playback/old/generation/chunk.m4s")));
        Assert.Equal(11, (await fixture.Service.CleanupAsync(["playback", "playback"], 0)).DeletedBytes);
    }
}
