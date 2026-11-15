using System.Globalization;
using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>验证 HLS 原始时间轴不依赖浏览器编辑列表处理或 ffprobe 的额外平移。</summary>
public sealed class FragmentedMp4TimelineTests
{
    /// <summary>B 帧重排和首帧延长均保留原始样本时钟，截断元数据会拒绝发布。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ReadsActualFragmentTimingAndRejectsTruncatedMetadata(int bFrames)
    {
        using var fixture = new MediaFixture();
        var path = await CreateAsync(fixture, bFrames, false);
        var timing = await FragmentedMp4Timeline.ReadAsync(fixture.DirectoryPath, CancellationToken.None);
        var inspected = await new TranscodeMediaInspector(fixture.Processes).InspectAsync(path);
        Assert.Equal(timing.PresentationMs, inspected.RawVideoStartMs);
        Assert.Equal(timing.DurationMs, inspected.FirstVideoSampleDurationMs);
        Assert.True(inspected.RawVideoEndMs > inspected.RawVideoStartMs);
        Assert.InRange(timing.DurationMs, 1000d / 15 - 0.01, 1000d / 15 + 30);
        Assert.InRange(timing.PresentationMs, 0, 200);
        if (bFrames > 0)
        {
            Assert.InRange(timing.PresentationMs, 1000d / 15, 200);
        }
        Assert.True(inspected.VideoBitrate > 0);
        Assert.True(inspected.AudioBitrate > 0);
        await File.WriteAllBytesAsync(Path.Combine(fixture.DirectoryPath, "init.mp4"), [0, 0, 0, 16, 109, 111, 111, 118, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FragmentedMp4Timeline.ReadAsync(fixture.DirectoryPath, CancellationToken.None));
    }

    /// <summary>有编辑列表的成品不能被标记为可用明确时间轴。</summary>
    [Fact]
    public async Task RejectsEditListsWithBrowserDependentPresentation()
    {
        using var fixture = new MediaFixture();
        await CreateAsync(fixture, 2, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => FragmentedMp4Timeline.ReadAsync(fixture.DirectoryPath, CancellationToken.None));
    }

    private static async Task<string> CreateAsync(MediaFixture fixture, int bFrames, bool editList)
    {
        var path = Path.Combine(fixture.DirectoryPath, "index.m3u8");
        await MediaProcessRunner.RunProcessAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "2",
            "-c:v", "libx264", "-preset", "medium", "-bf", bFrames.ToString(CultureInfo.InvariantCulture),
            "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-avoid_negative_ts", "make_zero",
            "-f", "hls", "-hls_time", "6", "-hls_segment_type", "fmp4", "-hls_playlist_type", "vod",
            "-hls_segment_options", "use_editlist=" + (editList ? "1" : "0"),
            "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_filename", Path.Combine(fixture.DirectoryPath, "chunk_%05d.m4s"), path
        ], CancellationToken.None);
        return path;
    }
}
