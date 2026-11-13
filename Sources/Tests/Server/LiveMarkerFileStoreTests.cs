using System.Text;
using Xunit;
using Yohuke.LiveRecorder.Domain.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证附件原文、独立副本、去重与删除清理提交点。</summary>
public sealed class LiveMarkerFileStoreTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "live-marker-file-tests", Guid.NewGuid().ToString("N"));

    private readonly AppPaths paths;
    private readonly LiveMarkerFileStore store;
    private readonly byte[] original = Encoding.UTF8.GetBytes("StartTime: 2026-11-12T00:00:00Z\nMarker: []\n");

    /// <summary>隔离文件测试目录。</summary>
    public LiveMarkerFileStoreTests()
    {
        paths = new(Path.Combine(root, "data"), Path.Combine(root, "records"));
        Directory.CreateDirectory(paths.RecordsDirectory);
        store = new(paths);
    }

    /// <summary>副本有独立稳定身份与管理归属，删除或校准不影响原件。</summary>
    [Fact]
    public async Task CopiesKeepOriginalBytesAndOwnershipButHaveIndependentAlignmentAndDeletion()
    {
        var file = await SeedAsync("source");
        var copies = await store.PlanCopiesAsync("source", "target", "target", new HashSet<string>());
        var copy = Assert.Single(copies);
        await store.CompleteCopyAsync(copy);
        await store.CompleteCopyAsync(copy);
        var target = Assert.Single(await store.ListAsync("target"));
        Assert.NotEqual(file.Id, target.Id);
        Assert.Equal(file.OriginId, target.OriginId);
        Assert.Equal(file.UploaderId, target.UploaderId);
        Assert.Equal(file.UploadedAtUtc, target.UploadedAtUtc);
        Assert.Equal(original,
            await File.ReadAllBytesAsync(Path.Combine(paths.RecordsDirectory, "target",
                LiveMarkerFileStore.DIRECTORY_NAME, target.Id, LiveMarkerFileStore.ORIGINAL_FILE_NAME)));
        await store.WriteMetadataAsync("target", target with { CalibrationMs = 1000 });
        Assert.Equal(0, (await store.ReadMetadataAsync("source", file.Id)).CalibrationMs);
        store.Delete("target", target.Id);
        Assert.Empty(await store.ListAsync("target"));
        Assert.Single(await store.ListAsync("source"));
    }

    /// <summary>同源同原文同校准去重，校准已独立变化的副本不能丢失。</summary>
    [Fact]
    public async Task RegroupDeduplicatesOnlyIdenticalOriginContentAndAlignment()
    {
        await SeedAsync("source");
        var copy = Assert.Single(await store.PlanCopiesAsync("source", "split", "split", new HashSet<string>()));
        await store.CompleteCopyAsync(copy);
        var seen = new HashSet<string>();
        Assert.Single(await store.PlanCopiesAsync("source", "grouped", "grouped", seen));
        Assert.Empty(await store.PlanCopiesAsync("split", "grouped", "grouped", seen));
        await store.WriteMetadataAsync("split", copy.File with { CalibrationMs = -500 });
        seen.Clear();
        Assert.Single(await store.PlanCopiesAsync("source", "changed", "changed", seen));
        Assert.Single(await store.PlanCopiesAsync("split", "changed", "changed", seen));
    }

    /// <summary>启动维护丢弃未发布与已删除暂存，不将其恢复为附件。</summary>
    [Fact]
    public async Task MaintenanceCleansInterruptedStagingWithoutChangingPublishedFiles()
    {
        await SeedAsync("source");
        foreach (var kind in new[] { "staging", "deleting" })
        {
            var interrupted = Path.Combine(paths.RecordsDirectory, ".work", "live-markers", kind,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(interrupted);
            await File.WriteAllTextAsync(Path.Combine(interrupted, "partial"), "incomplete");
        }

        await store.MaintainAsync();
        Assert.Single(await store.ListAsync("source"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(paths.RecordsDirectory, ".work", "live-markers"), "*",
            SearchOption.AllDirectories));
    }

    /// <summary>取消发布不创建可见附件且不留下临时文件。</summary>
    [Fact]
    public async Task CanceledPublicationLeavesNoVisibleAttachment()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var metadata = Metadata();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.PublishAsync("canceled", original, new(), metadata, cancellation.Token));
        Assert.Empty(await store.ListAsync("canceled"));
    }

    /// <summary>场次附件退役不会删除仍被媒体引用的其它文件。</summary>
    [Fact]
    public async Task RetiringAllAttachmentsPreservesUnrelatedMedia()
    {
        await SeedAsync("retired");
        var media = Path.Combine(paths.RecordsDirectory, "retired", "keep.flv");
        await File.WriteAllTextAsync(media, "referenced source");
        store.DeleteAll("retired");
        Assert.Empty(await store.ListAsync("retired"));
        Assert.Equal("referenced source", await File.ReadAllTextAsync(media));
    }

    /// <summary>恢复时不能将缺失原文的目标目录当成已经完整发布的副本。</summary>
    [Fact]
    public async Task ReplayedCopyRequiresItsOriginalFileToRemainComplete()
    {
        await SeedAsync("source");
        var copy = Assert.Single(await store.PlanCopiesAsync("source", "target", "target", new HashSet<string>()));
        await store.CompleteCopyAsync(copy);
        File.Delete(Path.Combine(paths.RecordsDirectory, "target", LiveMarkerFileStore.DIRECTORY_NAME, copy.File.Id,
            LiveMarkerFileStore.ORIGINAL_FILE_NAME));
        var failure =
            await Assert.ThrowsAsync<Yohuke.LiveRecorder.Application.InfrastructureException>(() =>
                store.CompleteCopyAsync(copy));
        Assert.Equal(409, failure.StatusCode);
    }

    private async Task<LiveMarkerFileMetadata> SeedAsync(string directory)
    {
        var metadata = Metadata();
        await store.PublishAsync(directory, original,
            new() { StartTime = DateTimeOffset.Parse("2026-11-12T00:00:00Z") }, metadata);
        return metadata;
    }

    private LiveMarkerFileMetadata Metadata()
    {
        var file = new LiveMarkerFileMetadata
        {
            UploaderId = "uploader", UploaderName = "作者", OriginalFileName = "记录.yaml",
            ContentHash = LiveMarkerFileStore.Hash(original), SourceTimeZoneId = "UTC"
        };
        return file with { OriginId = file.Id };
    }

    /// <summary>清理隔离测试目录。</summary>
    public void Dispose()
    {
        Directory.Delete(root, true);
    }
}
