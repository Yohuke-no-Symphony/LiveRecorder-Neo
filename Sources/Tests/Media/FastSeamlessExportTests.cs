using Yohuke.LiveRecorder.Application.Media;
using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>验证真实Fast导出跨连续录像分段时不丢包、不改变媒体内容或接缝时间轴。</summary>
public sealed class FastSeamlessExportTests
{
    /// <summary>完整分段导出与原流直接转MP4具有相同逐包时间轴，不累加切分带来的额外误差。</summary>
    [Theory]
    [InlineData("25", 48000, 50)]
    [InlineData("30000/1001", 44100, 60)]
    public async Task IndexedFastExportPreservesAllPacketsAndTheirRelativeTimeline(string frameRate, int sampleRate, int gopFrames)
    {
        using var fixture = new MediaFixture();
        var source = await SeamlessMediaTools.CreateSampleAsync(fixture, frameRate, sampleRate, gopFrames);
        var segments = (await fixture.RecordAsync(source, segmentBytes: 1000, title: "fast-continuous", captureCommitSnapshots: true))
            .Where(update => update.Kind == "Closed").ToArray();
        Assert.Equal(6, segments.Length);
        var inputs = new List<MediaInputSpan>();
        foreach (var segment in segments)
        {
            var path = Path.Combine(fixture.DirectoryPath, segment.RelativePath);
            var index = await FlvIndexSnapshot.ReadAsync(Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
                segment.CommittedBytes, CancellationToken.None);
            var first = index.Keyframes.First();
            var start = first.SessionOffsetMs - first.TimestampMs;
            Assert.Equal(segment.StartOffsetMs, start);
            Assert.Equal(segment.DurationMs, index.DurationMs);
            inputs.Add(new() { Path = path, SourceStartMs = 0, SessionStartMs = start, DurationMs = index.DurationMs });
        }
        var reference = Path.Combine(fixture.DirectoryPath, "source-reference.mp4");
        await MediaProcessRunner.RunProcessAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-copyts", "-i", source,
            "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-y", reference
        ], CancellationToken.None);
        var duration = inputs[^1].SessionStartMs + inputs[^1].DurationMs;
        var output = Path.Combine(fixture.DirectoryPath, "fast-continuous.mp4");

        var result = await fixture.Processor.ExportAsync(new()
        {
            Mode = "Fast", OutputPath = output, StartMs = 0, DurationMs = duration, Inputs = inputs
        }, new InlineProgress<double>(_ => { }), CancellationToken.None);

        Assert.Equal(0, result.ActualStartMs);
        Assert.Equal(duration, result.ActualEndMs);
        var original = await FastExportPacketProbe.ReadAsync(source);
        var expected = await FastExportPacketProbe.ReadAsync(reference);
        var actual = await FastExportPacketProbe.ReadAsync(output);
        var expectedFirst = expected.Packets.First(packet => packet.StreamKind == "video");
        var actualFirst = actual.Packets.First(packet => packet.StreamKind == "video");
        var shift = actualFirst.DtsMs - expectedFirst.DtsMs;
        var errors = new List<string>();
        foreach (var stream in new[] { "video", "audio" })
        {
            var sourcePackets = original.Packets.Where(packet => packet.StreamKind == stream).ToArray();
            var baseline = expected.Packets.Where(packet => packet.StreamKind == stream).ToArray();
            var exported = actual.Packets.Where(packet => packet.StreamKind == stream).ToArray();
            Assert.True(sourcePackets.Length > 200);
            Assert.Equal(sourcePackets.Select(packet => packet.DataHash), baseline.Select(packet => packet.DataHash));
            if (sourcePackets.Length != exported.Length)
            {
                errors.Add(stream + " packet count: expected " + sourcePackets.Length + ", exported " + exported.Length + ".");
            }
            var count = Math.Min(baseline.Length, exported.Length);
            var tolerance = actual.ClockTicksMs[stream] + 0.000001m;
            var firstContentMismatch = -1;
            var firstTimeMismatch = -1;
            var maximumError = 0m;
            for (var packet = 0; packet < count; packet++)
            {
                if (sourcePackets[packet].DataHash != exported[packet].DataHash && firstContentMismatch < 0)
                {
                    firstContentMismatch = packet;
                }
                var dtsError = Math.Abs(baseline[packet].DtsMs - (exported[packet].DtsMs - shift));
                var ptsError = Math.Abs(baseline[packet].PtsMs - (exported[packet].PtsMs - shift));
                maximumError = Math.Max(maximumError, Math.Max(dtsError, ptsError));
                if (Math.Max(dtsError, ptsError) > tolerance && firstTimeMismatch < 0)
                {
                    firstTimeMismatch = packet;
                }
            }
            if (firstContentMismatch >= 0)
            {
                errors.Add(stream + " coded packet content or order first differs at packet " + firstContentMismatch + ".");
            }
            if (firstTimeMismatch >= 0)
            {
                var before = baseline[firstTimeMismatch];
                var after = exported[firstTimeMismatch];
                errors.Add(FormattableString.Invariant(
                    $"{stream} packet {firstTimeMismatch} direct-MP4 DTS/PTS {before.DtsMs:0.######}/{before.PtsMs:0.######} ms; exported {after.DtsMs - shift:0.######}/{after.PtsMs - shift:0.######} ms. Maximum extra error {maximumError:0.######} ms; one output clock tick {tolerance:0.######} ms."));
            }
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
