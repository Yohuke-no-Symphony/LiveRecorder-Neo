using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Clipping.Worker.Storage;
using Yohuke.LiveRecorder.Clipping.Worker.Transport;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Clipping.Worker.Tests;

/// <summary>验证远程切片重建输入无需sidecar即可无损拼接相邻完整GOP。</summary>
public sealed class ClipFastSeamlessExportTests
{
    /// <summary>实际下载、重建、Fast导出链路保持跨范围的编码包及原生MP4时间轴。</summary>
    [Fact]
    public async Task PreparedAdjacentRangesExportSeamlesslyWithoutSidecars()
    {
        using var fixture = new ClipTestFixture();
        var (original, source) = await fixture.CreateAssignmentAsync("Fast");
        var indexPath = Path.Combine(fixture.Root, "indexed.index.jsonl");
        var start = original.Inputs[0].Range!.StartMs;
        var first = await IndexedFlvRange.PlanAsync(indexPath, source.LongLength, start, 1000);
        var second = await IndexedFlvRange.PlanAsync(indexPath, source.LongLength, first.EndMs, 1000);
        Assert.Equal(first.EndMs, second.StartMs);
        var sessionBase = original.ActualStartMs - start;
        var assignment = original with
        {
            Inputs = [Input("first", first, source, sessionBase), Input("second", second, source, sessionBase)],
            StartMs = sessionBase + first.StartMs, ActualStartMs = sessionBase + first.StartMs,
            EndMs = sessionBase + second.EndMs, ActualEndMs = sessionBase + second.EndMs
        };
        var referencePlan = await IndexedFlvRange.PlanAsync(indexPath, source.LongLength, first.StartMs, second.EndMs - first.StartMs);
        var referenceInput = Path.Combine(fixture.Root, "reference.flv");
        await using (var sourceStream = new MemoryStream(source, writable: false))
        await using (var destination = File.Create(referenceInput))
        {
            sourceStream.Position = referencePlan.StartOffset;
            await IndexedFlvRange.CopyAsync(sourceStream, referencePlan, destination);
        }
        var reference = Path.Combine(fixture.Root, "reference.mp4");
        var copied = await new WorkerProcessRunner().RunAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-copyts", "-f", "flv", "-i", referenceInput,
            "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-y", reference
        ], CancellationToken.None);
        Assert.True(copied.ExitCode == 0, copied.Diagnostic);
        using var handler = new ClipTestHttpHandler((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var from = checked((int)range.From!.Value);
            var until = checked((int)range.To!.Value);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(source[from..(until + 1)]) };
            response.Headers.ETag = new("\"fixed-source\"");
            response.Content.Headers.ContentRange = new(from, until, source.LongLength);
            return Task.FromResult(response);
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        using var workspace = new ClipWorkspace("worker");
        using var files = workspace.Acquire(fixture.Configuration, assignment);

        var manifest = await new ClipExecutor(transfer).ExecuteAsync(assignment, fixture.Configuration, files, CancellationToken.None);

        Assert.Equal(assignment.ActualStartMs, manifest.ActualStartMs);
        Assert.Equal(assignment.ActualEndMs, manifest.ActualEndMs);
        Assert.Equal(2, Directory.EnumerateFiles(files.DirectoryPath, "*.flv").Count());
        Assert.Empty(Directory.EnumerateFiles(files.DirectoryPath, "*.index.jsonl"));
        var expected = await PacketsAsync(reference);
        var actual = await PacketsAsync(files.OutputPath);
        var shift = actual.Packets.First(packet => packet.Stream == "video").Dts
            - expected.Packets.First(packet => packet.Stream == "video").Dts;
        foreach (var stream in new[] { "video", "audio" })
        {
            var before = expected.Packets.Where(packet => packet.Stream == stream).ToArray();
            var after = actual.Packets.Where(packet => packet.Stream == stream).ToArray();
            Assert.True(before.Length >= 20);
            Assert.Equal(before.Select(packet => packet.Hash), after.Select(packet => packet.Hash));
            var tolerance = Math.Max(expected.Ticks[stream], actual.Ticks[stream]) + 0.000001m;
            for (var index = 0; index < before.Length; index++)
            {
                Assert.InRange(Math.Abs(before[index].Dts - (after[index].Dts - shift)), 0, tolerance);
                Assert.InRange(Math.Abs(before[index].Pts - (after[index].Pts - shift)), 0, tolerance);
            }
        }
    }

    /// <summary>重建文件长度与已分配范围不一致时，拒绝以错误计划进入快速导出。</summary>
    [Fact]
    public async Task FastExportRejectsChangedPreparedInputLength()
    {
        using var fixture = new ClipTestFixture();
        var (assignment, _) = await fixture.CreateAssignmentAsync("Fast");
        using var workspace = new ClipWorkspace("worker");
        using var files = workspace.Acquire(fixture.Configuration, assignment);
        await File.WriteAllBytesAsync(Path.Combine(files.DirectoryPath, "input-00000.flv"), [0]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ClipExecutor.ExportAsync(assignment, fixture.Configuration, files, CancellationToken.None, null));

        Assert.False(File.Exists(files.OutputPath));
    }

    private static ClipInputSpan Input(string id, IndexedFlvRangePlan plan, byte[] source, double sessionBase) => new()
    {
        Id = id, SourceETag = "\"fixed-source\"", SourceVersion = "source-v1", SourceFileName = "indexed.flv",
        SourceStartMs = plan.StartMs, SessionStartMs = sessionBase + plan.StartMs, DurationMs = plan.EndMs - plan.StartMs,
        RangeSha256 = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan(checked((int)plan.StartOffset), checked((int)plan.Length)))),
        Range = new()
        {
            SourceLength = plan.SourceLength, StartOffset = plan.StartOffset, EndOffset = plan.EndOffset,
            StartMs = plan.StartMs, EndMs = plan.EndMs, VideoHeader = plan.VideoHeader, AudioHeader = plan.AudioHeader
        }
    };

    private static async Task<(List<(string Stream, string Hash, decimal Dts, decimal Pts)> Packets, Dictionary<string, decimal> Ticks)> PacketsAsync(string path)
    {
        var result = await new WorkerProcessRunner().RunAsync("ffprobe",
        [
            "-v", "error", "-show_packets", "-show_streams", "-show_data_hash", "sha256",
            "-show_entries", "packet=codec_type,dts_time,pts_time,data_hash:stream=codec_type,time_base", "-of", "json", path
        ], CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Diagnostic);
        using var document = JsonDocument.Parse(result.Output);
        var ticks = document.RootElement.GetProperty("streams").EnumerateArray().ToDictionary(
            stream => stream.GetProperty("codec_type").GetString()!, stream =>
            {
                var rate = stream.GetProperty("time_base").GetString()!.Split('/');
                return decimal.Parse(rate[0], CultureInfo.InvariantCulture) * 1000 / decimal.Parse(rate[1], CultureInfo.InvariantCulture);
            }, StringComparer.Ordinal);
        var packets = document.RootElement.GetProperty("packets").EnumerateArray().Select(packet =>
        (
            Stream: packet.GetProperty("codec_type").GetString()!, Hash: packet.GetProperty("data_hash").GetString()!,
            Dts: decimal.Parse(packet.GetProperty("dts_time").GetString()!, CultureInfo.InvariantCulture) * 1000,
            Pts: decimal.Parse(packet.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture) * 1000
        )).ToList();
        return (packets, ticks);
    }
}
