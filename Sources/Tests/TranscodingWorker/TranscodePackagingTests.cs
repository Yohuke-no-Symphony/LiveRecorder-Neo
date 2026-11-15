using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证可配置分片、关键帧和单媒体文件清单的兼容边界。</summary>
public sealed class TranscodePackagingTests
{
    /// <summary>分片与关键帧允许小数和非整除关系，生成参数使用固定小数格式。</summary>
    [Fact]
    public void PlannerUsesTheProfilePackagingSnapshot()
    {
        var profile = new TranscodeProfile { SegmentDurationSeconds = 7.5, KeyframeIntervalSeconds = 2.25, SingleFile = true };
        var plan = TranscodePlanner.Create("/input.flv", "/output", new()
        {
            Width = 1280, Height = 720, FrameRate = 30, DurationMs = 60000, VideoBitrate = 2000000,
            HasAudio = true, AudioBitrate = 96000
        }, profile, new() { Encoder = "libx264" }, 2);
        Assert.Equal("7.5", plan.Arguments[plan.Arguments.IndexOf("-hls_time") + 1]);
        Assert.Equal("68", plan.Arguments[plan.Arguments.IndexOf("-g") + 1]);
        Assert.Contains("expr:gte(t,n_forced*2.25)", plan.Arguments);
        Assert.Equal("independent_segments+single_file", plan.Arguments[plan.Arguments.IndexOf("-hls_flags") + 1]);
        Assert.Equal(Path.Combine("/output", "media.mp4"), plan.Arguments[plan.Arguments.IndexOf("-hls_segment_filename") + 1]);
        Assert.DoesNotContain(plan.Arguments, argument => argument.Contains("temp_file", StringComparison.Ordinal));
    }

    /// <summary>超出范围和非有限时长不能进入主站或 Worker 配方。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0.09)]
    [InlineData(300.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPackagingDurationsAreRejected(double value)
    {
        Assert.Throws<ArgumentException>(() => TranscodeConfigurationPolicy.ValidateProfile(new() { SegmentDurationSeconds = value }));
        Assert.Throws<ArgumentException>(() => TranscodeConfigurationPolicy.ValidateProfile(new() { KeyframeIntervalSeconds = value }));
    }

    /// <summary>区间端点和关键帧间隔大于目标分片时长均可配置。</summary>
    [Fact]
    public void ValidPackagingDurationsHaveNoDivisibilityRestriction()
    {
        TranscodeConfigurationPolicy.ValidateProfile(new() { SegmentDurationSeconds = 0.1, KeyframeIntervalSeconds = 300 });
        TranscodeConfigurationPolicy.ValidateProfile(new() { SegmentDurationSeconds = 300, KeyframeIntervalSeconds = 0.1 });
        TranscodeConfigurationPolicy.ValidateProfile(new() { SegmentDurationSeconds = 7.5, KeyframeIntervalSeconds = 2.25 });
    }

    /// <summary>旧多文件清单仍返回原来的媒体文件集合及六秒分片时长。</summary>
    [Fact]
    public void DefaultMultiFilePlaylistRemainsValid()
    {
        var parsed = TranscodePlaylistPolicy.Parse(MultiFilePlaylist(6), new(), new Dictionary<string, long>
        {
            ["index.m3u8"] = 0, ["init.mp4"] = 0, ["chunk_00000.m4s"] = 0
        }, 30);
        Assert.Equal(new[] { "chunk_00000.m4s", "init.mp4", "index.m3u8" }, parsed.FileNames);
        Assert.Equal(new[] { 6d }, parsed.SegmentDurations);
        Assert.Throws<InvalidDataException>(() => TranscodePlaylistPolicy.Parse(MultiFilePlaylist(6.6), new(), new Dictionary<string, long>
        {
            ["index.m3u8"] = 0, ["init.mp4"] = 0, ["chunk_00000.m4s"] = 0
        }, 30));
    }

    /// <summary>单文件清单允许同一媒体 URI 通过独立字节范围重复引用。</summary>
    [Fact]
    public void SingleFilePlaylistContainsTwoUniqueArtifacts()
    {
        var parsed = TranscodePlaylistPolicy.Parse(SingleFilePlaylist(), new() { SingleFile = true }, SingleFileLengths(), 30);
        Assert.Equal(new[] { "media.mp4", "index.m3u8" }, parsed.FileNames);
        Assert.Equal(new[] { 6d, 6d }, parsed.SegmentDurations);
    }

    /// <summary>字节范围不得重叠、溢出或引用未声明文件。</summary>
    [Theory]
    [InlineData("40@59")]
    [InlineData("41@60")]
    [InlineData("9223372036854775807@60")]
    [InlineData("0@60")]
    [InlineData("40")]
    public void InvalidSingleFileRangesAreRejected(string range)
    {
        Assert.Throws<InvalidDataException>(() => TranscodePlaylistPolicy.Parse(SingleFilePlaylist().Replace("40@60", range),
            new() { SingleFile = true }, SingleFileLengths(), 30));
    }

    /// <summary>存储模式、初始化 URI 与播放 URI 必须符合配方白名单。</summary>
    [Theory]
    [InlineData("media.mp4", "media.mp4.tmp")]
    [InlineData("media.mp4", "https://example.invalid/media.mp4")]
    [InlineData("#EXT-X-BYTERANGE:40@60\n", "")]
    [InlineData("URI=\"media.mp4\",BYTERANGE=\"10@0\"", "URI=\"init.mp4\"")]
    public void MixedOrExternalReferencesAreRejected(string before, string after)
    {
        Assert.Throws<InvalidDataException>(() => TranscodePlaylistPolicy.Parse(SingleFilePlaylist().Replace(before, after),
            new() { SingleFile = true }, SingleFileLengths(), 30));
    }

    /// <summary>结构预检允许低帧率量化，实际探测后按真实帧时长校验。</summary>
    [Fact]
    public void LowFrameRateQuantizationUsesTheActualFrameRate()
    {
        var profile = new TranscodeProfile { SegmentDurationSeconds = 0.1, KeyframeIntervalSeconds = 0.1 };
        var files = new Dictionary<string, long> { ["index.m3u8"] = 1, ["init.mp4"] = 1, ["chunk_00000.m4s"] = 1 };
        TranscodePlaylistPolicy.Parse(MultiFilePlaylist(10), profile, files);
        TranscodePlaylistPolicy.Parse(MultiFilePlaylist(10), profile, files, 0.1);
        Assert.Throws<InvalidDataException>(() => TranscodePlaylistPolicy.Parse(MultiFilePlaylist(10), profile, files, 30));
    }

    private static Dictionary<string, long> SingleFileLengths() => new() { ["index.m3u8"] = 1, ["media.mp4"] = 100 };

    private static string MultiFilePlaylist(double duration) => FormattableString.Invariant($"""
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-PLAYLIST-TYPE:VOD
        #EXT-X-MAP:URI="init.mp4"
        #EXTINF:{duration},
        chunk_00000.m4s
        #EXT-X-ENDLIST
        """);

    private static string SingleFilePlaylist() => """
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-PLAYLIST-TYPE:VOD
        #EXT-X-MAP:URI="media.mp4",BYTERANGE="10@0"
        #EXTINF:6,
        #EXT-X-BYTERANGE:50@10
        media.mp4
        #EXTINF:6,
        #EXT-X-BYTERANGE:40@60
        media.mp4
        #EXT-X-ENDLIST
        """;
}
