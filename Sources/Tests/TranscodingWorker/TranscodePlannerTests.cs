using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证转码参数、源码率上限和可跳转时间轴约束。</summary>
public sealed class TranscodePlannerTests
{
    /// <summary>HLS 输出使用 FFmpeg 可识别的正斜杠目录，并保持相对初始化文件名。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HlsOutputPathsUseForwardSlashes(bool singleFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker jobs", "转码输出");
        var plan = TranscodePlanner.Create(Path.Combine(directory, "input.mp4"), directory, Source(),
            new() { SingleFile = singleFile }, new() { Encoder = "libx264" }, 2);
        var segment = singleFile ? "media.mp4" : "chunk_%05d.m4s";

        Assert.Equal(Path.Combine(directory, "index.m3u8").Replace(Path.DirectorySeparatorChar, '/'), plan.Arguments[^1]);
        Assert.Equal(Path.Combine(directory, segment).Replace(Path.DirectorySeparatorChar, '/'),
            plan.Arguments[plan.Arguments.IndexOf("-hls_segment_filename") + 1]);
        Assert.Equal("init.mp4", plan.Arguments[plan.Arguments.IndexOf("-hls_fmp4_init_filename") + 1]);
    }

    /// <summary>Unix 路径中的合法反斜杠字符保持原样。</summary>
    [Fact]
    public void UnixDirectoryNamesPreserveLiteralBackslashes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "worker\\cache", "output");
        var plan = TranscodePlanner.Create("/input.mp4", directory, Source(), new(), new() { Encoder = "libx264" }, 2);

        Assert.Equal(Path.Combine(directory, "index.m3u8"), plan.Arguments[^1]);
        Assert.Equal(Path.Combine(directory, "chunk_%05d.m4s"), plan.Arguments[plan.Arguments.IndexOf("-hls_segment_filename") + 1]);
    }

    /// <summary>横竖屏以显示短边缩放，并保持帧率、音视频码率上限。</summary>
    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1080, 1920, 720, 1280)]
    public void UsesDisplayShortEdgeAndCapsVideoAudioAndFrameRate(int width, int height, int expectedWidth, int expectedHeight)
    {
        var source = Source() with { Width = width, Height = height };
        var profile = new TranscodeProfile { MaxFrameRate = 30 };
        var plan = TranscodePlanner.Create("/input.flv", "/output", source, profile, new() { Encoder = "libx264" }, 4);
        Assert.Equal(expectedWidth, plan.ExpectedMedia.Width);
        Assert.Equal(expectedHeight, plan.ExpectedMedia.Height);
        Assert.Equal(30, plan.ExpectedMedia.FrameRate);
        Assert.Equal(source.VideoBitrate, plan.ExpectedMedia.VideoBitrate);
        Assert.Equal(source.AudioBitrate, plan.ExpectedMedia.AudioBitrate);
        Assert.Contains("expr:gte(t,n_forced*2)", plan.Arguments);
        Assert.Equal("4", plan.Arguments[plan.Arguments.IndexOf("-threads:v") + 1]);
        Assert.Equal("use_editlist=0", plan.Arguments[plan.Arguments.IndexOf("-hls_segment_options") + 1]);
    }

    /// <summary>不允许升分辨率、未经色调映射的 HDR 或未知源码率。</summary>
    [Fact]
    public void RejectsUpscalingHdrAndUnknownSourceBitrate()
    {
        Assert.Equal("ResolutionAboveSource", Assert.Throws<NotSupportedException>(() => Plan(Source() with { Width = 640, Height = 360 })).Message);
        Assert.Equal("HdrUnsupported", Assert.Throws<NotSupportedException>(() => Plan(Source() with { IsHdr = true })).Message);
        Assert.Equal("SourceBitrateUnknown", Assert.Throws<NotSupportedException>(() => Plan(Source() with { VideoBitrate = 0 })).Message);
    }

    /// <summary>包统计以流式方式独立累计音频和视频码率。</summary>
    [Fact]
    public void PacketStatisticsAreStreamingAndSeparateTracks()
    {
        var video = new TranscodePacketStatistics();
        var audio = new TranscodePacketStatistics();
        var streams = new Dictionary<int, TranscodePacketStatistics> { [0] = video, [1] = audio };
        TranscodePacketStatistics.Parse("stream_index=0|pts_time=0.000|duration_time=1.0|size=1000", streams);
        TranscodePacketStatistics.Parse("stream_index=1|pts_time=0.000|duration_time=1.0|size=100", streams);
        TranscodePacketStatistics.Parse("stream_index=0|pts_time=1.000|duration_time=1.0|size=1000", streams);
        Assert.Equal(8000, video.Bitrate);
        Assert.Equal(800, audio.Bitrate);
    }

    private static TranscodePlan Plan(TranscodeMediaInfo source) => TranscodePlanner.Create("/in", "/out", source, new(), new() { Encoder = "libx264" }, 4);

    /// <summary>未指定上限时保留源帧率，包括超过 60 帧的输入。</summary>
    [Theory]
    [InlineData(null, 120)]
    [InlineData(90d, 90d)]
    public void FrameRateFollowsSourceUnlessProfileSetsALowerLimit(double? cap, double expected)
    {
        var plan = TranscodePlanner.Create("/in", "/out", Source() with { FrameRate = 120 },
            new() { MaxFrameRate = cap }, new() { Encoder = "libx264" }, 2);
        Assert.Equal(expected, plan.ExpectedMedia.FrameRate);
    }

    /// <summary>分片边界必须包含关键帧，解码时间不能倒退。</summary>
    [Fact]
    public void TimelineRejectsMissingBoundaryKeyframesAndBackwardDecodeTime()
    {
        var valid = new HlsPacketTimeline([0, 6], 30);
        valid.Add("pts_time=0.066|dts_time=0|flags=K_");
        valid.Add("pts_time=6.066|dts_time=6|flags=K_");
        valid.Complete();
        var missing = new HlsPacketTimeline([0, 6], 30);
        missing.Add("pts_time=0|dts_time=0|flags=K_");
        missing.Add("pts_time=6|dts_time=6|flags=__");
        Assert.Throws<InvalidDataException>(missing.Complete);
        var backwards = new HlsPacketTimeline([0], 30);
        backwards.Add("pts_time=1|dts_time=1|flags=K_");
        backwards.Add("pts_time=0|dts_time=0|flags=__");
        Assert.Throws<InvalidDataException>(backwards.Complete);
    }

    /// <summary>极低码率音频也不能被强制提升到固定下限。</summary>
    [Fact]
    public void LowBitrateAudioKeepsSourceCap()
    {
        var plan = Plan(Source() with { AudioBitrate = 6000 });
        Assert.Equal("6000", plan.Arguments[plan.Arguments.IndexOf("-b:a") + 1]);
    }

    private static TranscodeMediaInfo Source() => new()
    {
        Width = 1920, Height = 1080, FrameRate = 60, DurationMs = 60000,
        VideoBitrate = 2000000, AudioBitrate = 96000, HasAudio = true
    };
}
