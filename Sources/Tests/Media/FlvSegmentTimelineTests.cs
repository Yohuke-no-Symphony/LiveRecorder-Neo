using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>用短合成时间轴验证长期连接的32位时钟回绕和每文件时间基准。</summary>
public sealed class FlvSegmentTimelineTests
{
    /// <summary>跨signed及unsigned边界时仍保留各轨小幅交错回退，不误判成新一轮回绕。</summary>
    [Theory]
    [InlineData(0x7FFFFFF0L)]
    [InlineData(0xFFFFFFF0L)]
    public void ExpansionPreservesWrapAndInterleavedTrackTimestamps(long origin)
    {
        var timeline = new FlvSegmentTimeline();
        long[] offsets = [0, 8, 24, 18, 40, 35, 64];
        foreach (var offset in offsets)
        {
            var expected = origin + offset;
            Assert.Equal(expected, timeline.Expand(unchecked((int)(uint)expected)));
        }
    }

    /// <summary>按大小新开文件只切换文件基准，不会清除连接级已展开时钟。</summary>
    [Theory]
    [InlineData(0x7FFFFFF0L)]
    [InlineData(0xFFFFFFF0L)]
    public void NewFileOriginDoesNotResetTheConnectionClock(long origin)
    {
        var timeline = new FlvSegmentTimeline();
        Assert.Equal(origin, timeline.Expand(unchecked((int)(uint)origin)));
        timeline.BeginFile(origin);
        var boundary = timeline.Expand(unchecked((int)(uint)(origin + 32)));
        Assert.Equal(32u, unchecked((uint)timeline.ToFileTimestamp(boundary)));

        timeline.BeginFile(boundary);
        var video = timeline.Expand(unchecked((int)(uint)(origin + 64)));
        var audio = timeline.Expand(unchecked((int)(uint)(origin + 60)));
        Assert.Equal(origin + 64, video);
        Assert.Equal(origin + 60, audio);
        Assert.Equal(32u, unchecked((uint)timeline.ToFileTimestamp(video)));
        Assert.Equal(28u, unchecked((uint)timeline.ToFileTimestamp(audio)));
        Assert.Equal(boundary, timeline.FileOriginMs);
    }

    /// <summary>文件编码保留完整32位比特，不因int符号位变化截断合法时间戳。</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(0x80000000L)]
    [InlineData(0xFFFFFFFFL)]
    public void FileTimestampPreservesUnsignedBits(long localTimestamp)
    {
        var timeline = new FlvSegmentTimeline();
        timeline.BeginFile(1000);
        Assert.Equal((uint)localTimestamp, unchecked((uint)timeline.ToFileTimestamp(1000 + localTimestamp)));
    }

    /// <summary>超出文件可表达区间时明确拒绝，不把负值钳制到零或静默截断。</summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(0x100000000L)]
    public void FileTimestampRejectsValuesOutsideTheEncodedRange(long localTimestamp)
    {
        var timeline = new FlvSegmentTimeline();
        timeline.BeginFile(1000);
        Assert.Throws<InvalidDataException>(() => timeline.ToFileTimestamp(1000 + localTimestamp));
    }
}
