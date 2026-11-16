using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Yohuke.LiveRecorder.Clipping.Worker.Execution;
using Yohuke.LiveRecorder.Clipping.Worker.Storage;
using Yohuke.LiveRecorder.Clipping.Worker.Transport;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;

namespace Yohuke.LiveRecorder.Clipping.Worker.Tests;

/// <summary>Runs actual bounded-input clipping through FFmpeg and verifies task cleanup.</summary>
public sealed class ClipExecutorTests
{
    /// <summary>Exact and Fast modes export independently playable MP4s from middle ranges without downloading the original.</summary>
    [Theory]
    [InlineData("Exact", 1)]
    [InlineData("Exact", 3)]
    [InlineData("Exact", 128)]
    [InlineData("Fast", 7)]
    public async Task ExportsOnlyAssignedMiddleRange(string mode, int encodingThreads)
    {
        using var fixture = new ClipTestFixture();
        var (assignment, source) = await fixture.CreateAssignmentAsync(mode);
        var downloaded = 0L;
        using var handler = RangeHandler(source, bytes => downloaded += bytes);
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        using var workspace = new ClipWorkspace("worker");
        var configuration = fixture.Configuration with { ClipEncodingThreads = encodingThreads };
        var files = workspace.Acquire(configuration, assignment);
        try
        {
            var manifest = await new ClipExecutor(transfer).ExecuteAsync(assignment, configuration, files, CancellationToken.None);
            Assert.Equal(assignment.ActualStartMs, manifest.ActualStartMs);
            Assert.Equal(assignment.ActualEndMs, manifest.ActualEndMs);
            Assert.Equal(assignment.Inputs.Sum(input => input.Range!.EndOffset - input.Range.StartOffset), downloaded);
            Assert.True(downloaded < source.Length / 2);
            Assert.Equal(manifest.Bytes, new FileInfo(files.OutputPath).Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(files.OutputPath))), manifest.Sha256);
            var probe = await fixture.Processor.ProbeAsync(files.OutputPath);
            Assert.Equal("h264", probe.VideoCodec);
            Assert.True(probe.HasAudio);
            Assert.InRange(probe.DurationMs, assignment.ActualEndMs - assignment.ActualStartMs - 150,
                assignment.ActualEndMs - assignment.ActualStartMs + 250);
            var decoded = await new WorkerProcessRunner().RunAsync("ffmpeg",
                ["-v", "error", "-xerror", "-i", files.OutputPath, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"], CancellationToken.None);
            Assert.True(decoded.ExitCode == 0, decoded.Diagnostic);
            if (mode == "Exact")
            {
                var encoded = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(files.OutputPath));
                Assert.Contains("threads=" + encodingThreads + " ", encoded);
            }
            Assert.Empty(Directory.EnumerateFiles(files.DirectoryPath, "*.range*"));
        }
        finally
        {
            files.Dispose();
        }
        Assert.False(Directory.Exists(files.DirectoryPath));
    }

    /// <summary>Exact mode preserves a real gap between remote source ranges.</summary>
    [Fact]
    public async Task ExactExportPreservesRemoteGap()
    {
        using var fixture = new ClipTestFixture();
        var (assignment, source) = await fixture.CreateAssignmentAsync("Exact", gap: true);
        using var handler = RangeHandler(source);
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        using var workspace = new ClipWorkspace("worker");
        using var files = workspace.Acquire(fixture.Configuration, assignment);

        var manifest = await new ClipExecutor(transfer).ExecuteAsync(assignment, fixture.Configuration, files, CancellationToken.None);

        var probe = await fixture.Processor.ProbeAsync(files.OutputPath);
        Assert.InRange(probe.DurationMs, 2200, 2450);
        Assert.Equal(2300, manifest.ActualEndMs - manifest.ActualStartMs);
    }

    /// <summary>Cancellation during input transfer releases every per-attempt file.</summary>
    [Fact]
    public async Task CancelledTransferCleansWorkspace()
    {
        using var fixture = new ClipTestFixture();
        var assignment = ClipTestFixture.SmallAssignment();
        using var handler = new ClipTestHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancelled transfer unexpectedly continued.");
        });
        using var transfer = new ClipFileTransfer(fixture.Connection, handler);
        using var workspace = new ClipWorkspace("worker");
        var files = workspace.Acquire(fixture.Configuration, assignment);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ClipExecutor(transfer).ExecuteAsync(assignment, fixture.Configuration, files, cancellation.Token));
        }
        finally
        {
            files.Dispose();
        }
        Assert.False(Directory.Exists(files.DirectoryPath));
    }

    private static ClipTestHttpHandler RangeHandler(byte[] source, Action<long>? downloaded = null)
    {
        return new((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = checked((int)range.From!.Value);
            var end = checked((int)range.To!.Value);
            var bytes = source[start..(end + 1)];
            downloaded?.Invoke(bytes.Length);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new("\"fixed-source\"");
            response.Content.Headers.ContentRange = new(start, end, source.Length);
            return Task.FromResult(response);
        });
    }
}
