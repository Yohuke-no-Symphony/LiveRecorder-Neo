using System.Text.Json;
using Yohuke.LiveRecorder.Application.Media;
using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>验证同一直播连接内按大小切分时逐包内容和解码、呈现时间轴均连续。</summary>
public sealed class SeamlessRecordingSplitTests
{
    /// <summary>跨多个GOP切分后，音视频包各自保持完整数量、内容哈希和解码顺序。</summary>
    [Theory]
    [InlineData("25", 48000, 50)]
    [InlineData("30000/1001", 44100, 60)]
    public async Task ContinuousSplitPreservesEveryAudioAndVideoPacket(string frameRate, int sampleRate, int gopFrames)
    {
        using var fixture = new MediaFixture();
        var (source, segments) = await RecordAsync(fixture, frameRate, sampleRate, gopFrames);
        var expected = await SeamlessMediaTools.PacketsAsync(source);
        var actual = new List<SeamlessMediaPacket>();
        foreach (var segment in segments)
        {
            actual.AddRange(await SeamlessMediaTools.PacketsAsync(Path.Combine(fixture.DirectoryPath, segment.RelativePath)));
        }

        AssertPacketContent(expected, actual);
    }

    /// <summary>已发布起点与sidecar起点都能精确还原源流的每个DTS和PTS，不能累加估算帧长。</summary>
    [Theory]
    [InlineData("25", 48000, 50)]
    [InlineData("30000/1001", 44100, 60)]
    public async Task PublishedOffsetsRestoreTheOriginalPacketTimeline(string frameRate, int sampleRate, int gopFrames)
    {
        using var fixture = new MediaFixture();
        var (source, segments) = await RecordAsync(fixture, frameRate, sampleRate, gopFrames);
        var expected = await SeamlessMediaTools.PacketsAsync(source);
        var sourceOrigin = expected.Min(packet => packet.DtsMs);
        var fromUpdates = new List<SeamlessMediaPacket>();
        var fromIndexes = new List<SeamlessMediaPacket>();
        foreach (var segment in segments)
        {
            var packets = await SeamlessMediaTools.PacketsAsync(Path.Combine(fixture.DirectoryPath, segment.RelativePath));
            var index = (await File.ReadAllLinesAsync(Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath)))
                .Select(line => JsonSerializer.Deserialize<FlvIndexEntry>(line, MediaJson.Options)!).ToArray();
            var keyframes = index.Where(entry => entry.Kind == "Keyframe").ToArray();
            Assert.NotEmpty(keyframes);
            var indexOffset = (decimal)(keyframes[0].SessionOffsetMs - keyframes[0].TimestampMs);
            Assert.Equal((decimal)segment.StartOffsetMs, indexOffset);
            foreach (var keyframe in keyframes)
            {
                Assert.NotNull(keyframe.OriginalTimestampMs);
                AssertTimestamp((decimal)keyframe.OriginalTimestampMs.Value - sourceOrigin, (decimal)keyframe.SessionOffsetMs,
                    "Source keyframe vs sidecar, segment " + segment.RelativePath);
            }
            fromUpdates.AddRange(packets.Select(packet => packet.AtOffset((decimal)segment.StartOffsetMs)));
            fromIndexes.AddRange(packets.Select(packet => packet.AtOffset(indexOffset)));
        }

        var normalized = expected.Select(packet => packet.AtOffset(-sourceOrigin)).ToArray();
        AssertPacketTimeline(normalized, fromUpdates);
        AssertPacketTimeline(normalized, fromIndexes);
    }

    /// <summary>仅依赖输出FLV元数据执行ffmpeg concat流复制，仍可还原全部包及时间戳。</summary>
    [Theory]
    [InlineData("25", 48000, 50)]
    [InlineData("30000/1001", 44100, 60)]
    public async Task SplitMetadataSupportsLosslessFfmpegConcat(string frameRate, int sampleRate, int gopFrames)
    {
        using var fixture = new MediaFixture();
        var (source, segments) = await RecordAsync(fixture, frameRate, sampleRate, gopFrames);
        var expected = await SeamlessMediaTools.PacketsAsync(source);
        var output = await SeamlessMediaTools.ConcatAsync(fixture, segments.Select(segment => Path.Combine(fixture.DirectoryPath, segment.RelativePath)));
        var actual = await SeamlessMediaTools.PacketsAsync(output);
        var shift = actual.Min(packet => packet.DtsMs) - expected.Min(packet => packet.DtsMs);

        AssertPacketTimeline(expected, actual.Select(packet => packet.AtOffset(-shift)).ToArray());
    }

    /// <summary>切分封口不得缩短已公布的进度与提交快照，末段须包含最后音视频包的呈现尾部。</summary>
    [Theory]
    [InlineData("25", 48000, 50)]
    [InlineData("30000/1001", 44100, 60)]
    public async Task ClosingSegmentsNeverRegressesPublishedDurationsAndKeepsTheFinalTail(string frameRate, int sampleRate, int gopFrames)
    {
        using var fixture = new MediaFixture();
        var source = await SeamlessMediaTools.CreateSampleAsync(fixture, frameRate, sampleRate, gopFrames);
        var updates = await fixture.RecordAsync(source, segmentBytes: 1000, title: "monotonic", captureCommitSnapshots: true);
        var closed = updates.Where(update => update.Kind == "Closed").ToArray();
        Assert.True(closed.Length >= 5);
        foreach (var group in updates.GroupBy(update => update.SegmentId))
        {
            Assert.Equal("Opened", group.First().Kind);
            Assert.Equal("Closed", group.Last().Kind);
            Assert.Contains(group, update => update.Kind == "Committed" && update.DurationMs > 0);
            var duration = 0d;
            var sequence = 0L;
            foreach (var update in group)
            {
                Assert.Equal(group.First().StartOffsetMs, update.StartOffsetMs);
                Assert.True(update.DurationMs >= duration,
                    FormattableString.Invariant($"{update.RelativePath} {update.Kind} duration shrank from {duration:0.###} to {update.DurationMs:0.###} ms."));
                duration = update.DurationMs;
                if (update.CommitSnapshot is { } snapshot)
                {
                    Assert.Equal(update.DurationMs, snapshot.DurationMs);
                    Assert.Equal(update.StartOffsetMs, snapshot.StartOffsetMs);
                    Assert.True(snapshot.CommitSequence > sequence);
                    sequence = snapshot.CommitSequence;
                }
            }
            Assert.True(group.Last().CommitSnapshot?.IsClosed);
            var index = (await File.ReadAllLinesAsync(Path.Combine(fixture.DirectoryPath, group.Last().IndexRelativePath)))
                .Select(line => JsonSerializer.Deserialize<FlvIndexEntry>(line, MediaJson.Options)!)
                .Where(entry => entry.Kind == "Committed").ToArray();
            var committed = 0d;
            foreach (var entry in index)
            {
                Assert.True(entry.TimestampMs >= committed, "The sidecar committed watermark regressed at a segment boundary.");
                committed = entry.TimestampMs;
            }
            Assert.Equal(group.Last().DurationMs, committed);
        }
        for (var segment = 0; segment < closed.Length - 1; segment++)
        {
            AssertTimestamp((decimal)(closed[segment].StartOffsetMs + closed[segment].DurationMs),
                (decimal)closed[segment + 1].StartOffsetMs, "Adjacent published segment boundaries");
        }
        var last = closed[^1];
        var packets = await SeamlessMediaTools.PacketsAsync(Path.Combine(fixture.DirectoryPath, last.RelativePath));
        var videoEnd = packets.Where(packet => packet.StreamKind == "video").Max(packet => packet.PtsMs)
            + (frameRate == "25" ? 40m : 1001m / 30);
        var audioEnd = packets.Where(packet => packet.StreamKind == "audio").Max(packet => packet.PtsMs) + 1024000m / sampleRate;
        Assert.True((decimal)last.DurationMs >= Math.Max(videoEnd, audioEnd) - 1m,
            FormattableString.Invariant($"The final segment ends at {last.DurationMs:0.###} ms before its final packet tail at {Math.Max(videoEnd, audioEnd):0.###} ms."));
    }

    private static async Task<(string Source, MediaRecordingUpdate[] Segments)> RecordAsync(MediaFixture fixture, string frameRate, int sampleRate, int gopFrames)
    {
        var source = await SeamlessMediaTools.CreateSampleAsync(fixture, frameRate, sampleRate, gopFrames);
        var updates = await fixture.RecordAsync(source, segmentBytes: 1000, title: "continuous", captureCommitSnapshots: true);
        var segments = updates.Where(update => update.Kind == "Closed").ToArray();
        Assert.True(segments.Length >= 5, "The twelve-second fixture must exercise several automatic GOP splits.");
        Assert.Equal(0, segments[0].StartOffsetMs);
        Assert.Equal(segments.Length, segments.Select(segment => segment.SegmentId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(segments, segment => Assert.True(segment.CommitSnapshot?.IsClosed));
        return (source, segments);
    }

    private static void AssertPacketContent(IReadOnlyCollection<SeamlessMediaPacket> expected, IReadOnlyCollection<SeamlessMediaPacket> actual)
    {
        foreach (var stream in new[] { "video", "audio" })
        {
            var source = expected.Where(packet => packet.StreamKind == stream).ToArray();
            var output = actual.Where(packet => packet.StreamKind == stream).ToArray();
            Assert.True(source.Length > 200, "The source must contain enough " + stream + " packets to cross multiple splits.");
            Assert.Equal(source.Length, output.Length);
            for (var packet = 0; packet < source.Length; packet++)
            {
                Assert.True(source[packet].DataHash == output[packet].DataHash,
                    stream + " packet " + packet + " differs after splitting or concatenation.");
            }
        }
    }

    private static void AssertPacketTimeline(IReadOnlyCollection<SeamlessMediaPacket> expected, IReadOnlyCollection<SeamlessMediaPacket> actual)
    {
        AssertPacketContent(expected, actual);
        foreach (var stream in new[] { "video", "audio" })
        {
            var source = expected.Where(packet => packet.StreamKind == stream).ToArray();
            var output = actual.Where(packet => packet.StreamKind == stream).ToArray();
            for (var packet = 0; packet < source.Length; packet++)
            {
                AssertTimestamp(source[packet].DtsMs, output[packet].DtsMs, stream + " packet " + packet + " DTS");
                AssertTimestamp(source[packet].PtsMs, output[packet].PtsMs, stream + " packet " + packet + " PTS");
            }
        }
    }

    private static void AssertTimestamp(decimal expectedMs, decimal actualMs, string description)
    {
        Assert.True(Math.Abs(expectedMs - actualMs) <= 0.001m,
            FormattableString.Invariant($"{description}: expected {expectedMs:0.###} ms, received {actualMs:0.###} ms."));
    }
}
