using Microsoft.Extensions.Options;
using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Media;

namespace Yohuke.LiveRecorder.Grouping.Tests;

/// <summary>以真实编解码工具验证分组后的根引用可用于播放、切片和修复。</summary>
public sealed class GroupedMediaTests
{
    /// <summary>归组不复制 canonical，媒体消费者通过根引用读取有效 FLV 和索引。</summary>
    [Fact]
    public async Task RootReferencesSupportNativeClipPlaybackAndRepair()
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var native = await fixture.SeedNativeAsync();
        var second = await fixture.SeedAsync(10000);
        var before = fixture.ArchiveHashes();
        var grouped = await fixture.Service().GroupAsync(native.Session.Id, [second.Session.Id], null);
        Assert.Equal(before.OrderBy(pair => pair.Key), fixture.ArchiveHashes().OrderBy(pair => pair.Key));
        var detail = await fixture.Library.GetAsync(grouped.Id);
        var segment = detail.Segments.OrderBy(item => item.StartOffsetMs).First();
        var source = MediaPaths.ResolveSegment(fixture.Root, grouped.DirectoryPath, segment);
        var index = MediaPaths.ResolveSegmentIndex(fixture.Root, grouped.DirectoryPath, segment);
        Assert.Equal("h264", (await fixture.Processor.ProbeAsync(source)).VideoCodec);
        var window = await new IndexedPlaybackWindowBuilder(fixture.Processes, Options.Create(new MediaToolOptions()), Options.Create(new PlaybackWindowOptions())).BuildAsync(new()
        {
            SourcePath = source, IndexPath = index, CommittedBytes = segment.CommittedBytes, IsClosed = true,
            SourceStartMs = 200, DurationMs = 500, OutputDirectory = Path.Combine(fixture.Root, ".derived", "window")
        }, CancellationToken.None);
        Assert.True(File.Exists(window.PlaylistPath));
        var start = segment.StartOffsetMs + 200;
        var clip = Path.Combine(fixture.Root, ".derived", "clip.mp4");
        await fixture.Processor.ExportAsync(new()
        {
            OutputPath = clip, StartMs = start, DurationMs = 500,
            Inputs = SessionTimeline.ExportSegmentSpans([segment], start, start + 500,
                item => MediaPaths.ResolveSegment(fixture.Root, grouped.DirectoryPath, item))
        }, new GroupingProgress<double>(_ => { }), CancellationToken.None);
        Assert.InRange((await fixture.Processor.ProbeAsync(clip)).DurationMs, 400, 650);
        var repair = await fixture.Processor.RepairAsync(source, Path.Combine(fixture.Root, ".derived", "repaired.flv"), new GroupingProgress<double>(_ => { }), CancellationToken.None);
        Assert.NotEmpty(repair.Outputs);
        foreach (var original in before)
        {
            Assert.True(File.Exists(original.Key));
            Assert.Equal(original.Value, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(original.Key))));
        }
    }
}
