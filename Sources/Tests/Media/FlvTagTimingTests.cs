using BililiveRecorder.Flv;
using BililiveRecorder.Flv.Pipeline;
using BililiveRecorder.Flv.Pipeline.Actions;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>验证FLV媒体时间戳、composition offset和AAC帧时长的独立解析边界。</summary>
public sealed class FlvTagTimingTests
{
    /// <summary>AVC和HEVC的composition offset按有符号24位读取，不改变解码时间戳。</summary>
    [Theory]
    [InlineData(7, -8388608)]
    [InlineData(7, -93)]
    [InlineData(7, -1)]
    [InlineData(7, 0)]
    [InlineData(7, 93)]
    [InlineData(7, 8388607)]
    [InlineData(12, -93)]
    [InlineData(12, 93)]
    public void CompositionOffsetPreservesSigned24BitValues(int codec, int offset)
    {
        using var body = Body((byte)(0x10 | codec), 1, (byte)((offset >> 16) & 0xFF), (byte)((offset >> 8) & 0xFF), (byte)(offset & 0xFF));
        var tag = new Tag { Type = TagType.Video, Timestamp = 1000, BinaryData = body };

        Assert.Equal(offset, FlvTagTiming.CompositionOffsetMs(tag));
        Assert.Equal(1000L + offset, FlvTagTiming.PresentationTimestampMs(tag));
        Assert.Equal(1000L, FlvTagTiming.TimestampMs(tag));
        Assert.Equal(1000, tag.Timestamp);
    }

    /// <summary>FLV时间戳的最高位属于无符号时钟，不会变成负的DTS或PTS。</summary>
    [Theory]
    [InlineData(int.MinValue, 2147483648L)]
    [InlineData(-1, 4294967295L)]
    public void TimestampPreservesAllUnsignedBits(int timestamp, long expected)
    {
        var tag = new Tag { Type = TagType.Audio, Timestamp = timestamp };

        Assert.Equal(expected, FlvTagTiming.TimestampMs(tag));
        Assert.Equal(expected, FlvTagTiming.PresentationTimestampMs(tag));
    }

    /// <summary>Script、音频和视频配置或结束包不包含视频媒体composition offset。</summary>
    [Theory]
    [InlineData(TagType.Script, TagFlag.None, 1)]
    [InlineData(TagType.Audio, TagFlag.None, 1)]
    [InlineData(TagType.Video, TagFlag.Header, 0)]
    [InlineData(TagType.Video, TagFlag.End, 2)]
    public void NonVideoMediaHasNoCompositionOffset(TagType type, TagFlag flag, int packetType)
    {
        using var body = Body(0x17, (byte)packetType, 0xFF, 0xFF, 0xA3);
        var tag = new Tag { Type = type, Flag = flag, Timestamp = 1000, BinaryData = body };

        Assert.Equal(0, FlvTagTiming.CompositionOffsetMs(tag));
        Assert.Equal(1000L, FlvTagTiming.PresentationTimestampMs(tag));
    }

    /// <summary>AAC-LC的索引或显式采样率以及960样本标记共同确定帧时长。</summary>
    [Theory]
    [InlineData(new byte[] { 0xAF, 0, 0x12, 0x10 }, 44100, 1024)]
    [InlineData(new byte[] { 0xAF, 0, 0x12, 0x14 }, 44100, 960)]
    [InlineData(new byte[] { 0xAF, 0, 0x11, 0x90 }, 48000, 1024)]
    [InlineData(new byte[] { 0xAF, 0, 0x11, 0x94 }, 48000, 960)]
    [InlineData(new byte[] { 0xAF, 0, 0x17, 0x80, 0x5D, 0xC0, 0x10 }, 48000, 1024)]
    [InlineData(new byte[] { 0xAF, 0, 0x17, 0x80, 0x5D, 0xC0, 0x14 }, 48000, 960)]
    public void AacFrameDurationHonorsSampleRateAndFrameLength(byte[] payload, int sampleRate, int samples)
    {
        using var body = Body(payload);
        var tag = new Tag { Type = TagType.Audio, Flag = TagFlag.Header, BinaryData = body };

        var duration = FlvAudioTiming.FrameDurationMs(tag);

        Assert.NotNull(duration);
        Assert.Equal(samples * 1000d / sampleRate, duration.Value, 12);
    }

    /// <summary>不完整ASC及保留采样率编码不产生看似有效的音频时长。</summary>
    [Theory]
    [InlineData(new byte[] { 0xAF, 0, 0x12 })]
    [InlineData(new byte[] { 0xAF, 0, 0x17, 0x80, 0x5D, 0xC0 })]
    [InlineData(new byte[] { 0xAF, 0, 0x16, 0x90 })]
    [InlineData(new byte[] { 0xAF, 0, 0x17, 0x10 })]
    public void InvalidAudioSpecificConfigDoesNotInventAFrameDuration(byte[] payload)
    {
        using var body = Body(payload);
        var tag = new Tag { Type = TagType.Audio, Flag = TagFlag.Header, BinaryData = body };

        Assert.Null(FlvAudioTiming.FrameDurationMs(tag));
    }

    /// <summary>同一ASC字节只能由音频配置标签更新帧时长，媒体和其它标签不会误触发。</summary>
    [Theory]
    [InlineData(TagType.Audio, TagFlag.None)]
    [InlineData(TagType.Video, TagFlag.Header)]
    [InlineData(TagType.Script, TagFlag.None)]
    public void AudioFrameDurationRequiresAnAudioConfiguration(TagType type, TagFlag flag)
    {
        using var body = Body(0xAF, 0, 0x12, 0x10);
        var tag = new Tag { Type = type, Flag = flag, BinaryData = body };

        Assert.Null(FlvAudioTiming.FrameDurationMs(tag));
    }

    /// <summary>大小切分使用后续修复规则的最终动作，仅媒体数据参与文件时间原点计算。</summary>
    [Fact]
    public void SplitRuleReadsRepairedMediaAndIgnoresScriptAndHeaders()
    {
        var target = new FlvArchiveTargetProvider(new()
        {
            OutputDirectory = Path.GetTempPath(), StartedAtUtc = DateTime.UnixEpoch,
            SessionStartedAtUtc = DateTime.UnixEpoch, FileTimeZoneId = "UTC"
        }, new InlineProgress<MediaRecordingUpdate>(_ => { }));
        var original = new Tag { Type = TagType.Video, Timestamp = 100 };
        var audio = new Tag { Type = TagType.Audio, Timestamp = 3000 };
        var video = new Tag { Type = TagType.Video, Flag = TagFlag.Keyframe, Timestamp = 3010 };
        var header = new Tag { Type = TagType.Video, Flag = TagFlag.Header, Timestamp = -1000 };
        var script = new Tag { Type = TagType.Script, Timestamp = -2000 };
        var repaired = new PipelineDataAction([header, script, audio, video]);
        var context = new FlvProcessingContext(new PipelineDataAction([original]), new Dictionary<object, object?>());

        new GopSizeSplitRule(1, target).Run(context, () =>
        {
            context.Actions.Clear();
            context.Actions.Add(repaired);
        });

        Assert.Same(repaired, Assert.Single(context.Actions));
        Assert.Equal(0, audio.Timestamp);
        Assert.Equal(10, video.Timestamp);
        Assert.Equal(-1000, header.Timestamp);
        Assert.Equal(-2000, script.Timestamp);
        Assert.Equal(100, original.Timestamp);
    }

    private static MemoryStream Body(params byte[] data) => new(data, 0, data.Length, writable: false, publiclyVisible: true);
}
