using System.Security.Cryptography;
using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Validates exported files with actual FFmpeg and ffprobe processes.</summary>
public sealed class MediaExportTests
{
    /// <summary>Invalid Exact encoding thread settings are rejected before any source is read or output is written.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public async Task ExactExportRejectsInvalidEncodingThreads(int encodingThreads)
    {
        using var fixture = new MediaFixture();
        var output = Path.Combine(fixture.DirectoryPath, "invalid-threads.mp4");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Processor.ExportAsync(new()
        {
            OutputPath = output, DurationMs = 1000, EncodingThreads = encodingThreads,
            Inputs = [new() { Path = "missing.flv", DurationMs = 1000 }]
        }, new InlineProgress<double>(_ => { }), CancellationToken.None));

        Assert.False(File.Exists(output));
    }

    /// <summary>Exact merge normalizes different encoding epochs and handles quoted filesystem paths.</summary>
    [Fact]
    public async Task ExactMergeNormalizesDifferentResolutions()
    {
        using var fixture = new MediaFixture();
        var first = await fixture.CreateSampleAsync("merge-first.flv", "160x90", 2);
        var second = await fixture.CreateSampleAsync("merge-second.flv", "192x108", 2);
        var output = Path.Combine(fixture.DirectoryPath, "it's a merged clip", "merged.mp4");
        var result = await fixture.Processor.ExportAsync(new()
        {
            OutputPath = output,
            DurationMs = 4000,
            Inputs =
            [
                new() { Path = first, SessionStartMs = 0, DurationMs = 2000 },
                new() { Path = second, SessionStartMs = 2000, DurationMs = 2000 }
            ]
        }, new InlineProgress<double>(_ => { }), CancellationToken.None);
        var probe = await fixture.Processor.ProbeAsync(output);
        Assert.InRange(probe.DurationMs, 3900, 4150);
        Assert.Equal(160, probe.Width);
        Assert.Equal(90, probe.Height);
        Assert.Equal(4000, result.ActualEndMs);
    }

    /// <summary>Fast merge rejects incompatible video before committing an output.</summary>
    [Fact]
    public async Task FastMergeRejectsIncompatibleEncodingEpochs()
    {
        using var fixture = new MediaFixture();
        var first = await fixture.CreateSampleAsync("fast-first.flv", "160x90", 2);
        var second = await fixture.CreateSampleAsync("fast-second.flv", "192x108", 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Processor.ExportAsync(new()
        {
            OutputPath = Path.Combine(fixture.DirectoryPath, "incompatible.mp4"),
            Mode = "Fast",
            DurationMs = 4000,
            Inputs =
            [
                new() { Path = first, SessionStartMs = 0, DurationMs = 2000 },
                new() { Path = second, SessionStartMs = 2000, DurationMs = 2000 }
            ]
        }, new InlineProgress<double>(_ => { }), CancellationToken.None));
    }

    /// <summary>Fast mode reports the keyframe-expanded range from its recording index.</summary>
    [Fact]
    public async Task FastExportProducesPlayableIndexedKeyframeRange()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync();
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var source = Path.Combine(fixture.DirectoryPath, segment.RelativePath);
        var output = Path.Combine(fixture.DirectoryPath, "fast-indexed.mp4");
        var result = await fixture.Processor.ExportAsync(new()
        {
            OutputPath = output,
            StartMs = 500,
            DurationMs = 1000,
            Mode = "Fast",
            Inputs = [new() { Path = source, SessionStartMs = 0, SourceStartMs = 0, DurationMs = segment.DurationMs }]
        }, new InlineProgress<double>(_ => { }), CancellationToken.None);
        Assert.InRange(result.ActualStartMs, 0, 500);
        Assert.True(result.ActualEndMs >= 1500);
        Assert.True((await fixture.Processor.ProbeAsync(output)).DurationMs > 0);
    }

    /// <summary>Changing encoding headers does not discard later repaired output files.</summary>
    [Fact]
    public async Task RepairPreservesMultipleEncodingEpochs()
    {
        using var fixture = new MediaFixture();
        var first = await fixture.CreateSampleAsync("first.flv", "160x90", 2);
        var second = await fixture.CreateSampleAsync("second.flv", "192x108", 2);
        var combined = Path.Combine(fixture.DirectoryPath, "epochs.flv");
        await File.WriteAllBytesAsync(combined, (await File.ReadAllBytesAsync(first)).Concat((await File.ReadAllBytesAsync(second)).Skip(13)).ToArray());
        var result = await fixture.Processor.RepairAsync(combined, Path.Combine(fixture.DirectoryPath, "epochs-fixed.flv"), new InlineProgress<double>(_ => { }), CancellationToken.None);
        Assert.True(result.Outputs.Count >= 2);
        var widths = new List<int>();
        foreach (var output in result.Outputs)
        {
            widths.Add((await fixture.Processor.ProbeAsync(output.Path)).Width);
            Assert.True(File.Exists(output.IndexPath));
        }
        Assert.Contains(160, widths);
        Assert.Contains(192, widths);
    }

    /// <summary>Timestamp repair retains its before/after mapping in the derivative index.</summary>
    [Fact]
    public async Task RepairFixesTimestampJumpAndWritesOriginalMapping()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync();
        var broken = Path.Combine(fixture.DirectoryPath, "jump.flv");
        await File.WriteAllBytesAsync(broken, FlvSampleEditor.AddTimestampJump(await File.ReadAllBytesAsync(sample)));
        var result = await fixture.Processor.RepairAsync(broken, Path.Combine(fixture.DirectoryPath, "jump-fixed.flv"), new InlineProgress<double>(_ => { }), CancellationToken.None);
        var mappingFound = false;
        foreach (var output in result.Outputs)
        {
            Assert.InRange((await fixture.Processor.ProbeAsync(output.Path)).DurationMs, 3500, 4500);
            foreach (var line in await File.ReadAllLinesAsync(output.IndexPath))
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("originalTimestampMs").ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    mappingFound |= root.GetProperty("originalTimestampMs").GetDouble() - root.GetProperty("timestampMs").GetDouble() > 20000;
                }
            }
        }
        Assert.True(mappingFound);
    }

    /// <summary>Cancellation kills active media work and never commits a partial final output.</summary>
    [Fact]
    public async Task CanceledExportDoesNotCommitPartialOutput()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync();
        var output = Path.Combine(fixture.DirectoryPath, "canceled.mp4");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.ExportAsync(new()
        {
            OutputPath = output,
            DurationMs = 3000,
            Inputs = [new() { Path = source, DurationMs = 3000 }]
        }, new InlineProgress<double>(value =>
        {
            if (value > 0)
            {
                cancellation.Cancel();
            }
        }), cancellation.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, ".export-*"));
    }

    /// <summary>Preserves a real gap as silent placeholder media in an exact export.</summary>
    [Fact]
    public async Task ExactExportPreservesGapAndRequestedDuration()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync();
        var output = Path.Combine(fixture.DirectoryPath, "clip.mp4");
        var result = await fixture.Processor.ExportAsync(new()
        {
            OutputPath = output,
            StartMs = 0,
            DurationMs = 3000,
            Inputs =
            [
                new() { Path = source, SourceStartMs = 500, SessionStartMs = 0, DurationMs = 1000 },
                new() { SessionStartMs = 1000, DurationMs = 1000 },
                new() { Path = source, SourceStartMs = 2500, SessionStartMs = 2000, DurationMs = 1000 }
            ]
        }, new InlineProgress<double>(_ => { }), CancellationToken.None);
        var probe = await fixture.Processor.ProbeAsync(output);
        Assert.InRange(probe.DurationMs, 2900, 3150);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.True(probe.HasAudio);
        Assert.Equal(0, result.ActualStartMs);
        Assert.Equal(3000, result.ActualEndMs);
        Assert.True(result.Bytes > 0);
    }

    /// <summary>Fast mode cannot silently remove requested wall-clock gaps.</summary>
    [Fact]
    public async Task FastExportRejectsGapsWithoutWritingSuccessOutput()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync();
        var output = Path.Combine(fixture.DirectoryPath, "fast.mp4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Processor.ExportAsync(new()
        {
            OutputPath = output,
            Mode = "Fast",
            StartMs = 0,
            DurationMs = 2000,
            Inputs =
            [
                new() { Path = source, SessionStartMs = 0, DurationMs = 1000 },
                new() { SessionStartMs = 1000, DurationMs = 1000 }
            ]
        }, new InlineProgress<double>(_ => { }), CancellationToken.None));
        Assert.False(File.Exists(output));
    }

    /// <summary>Repair is a new derivative and leaves its source bytes unchanged.</summary>
    [Fact]
    public async Task RepairReturnsEveryOutputAndPreservesSource()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync();
        var original = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var result = await fixture.Processor.RepairAsync(source, Path.Combine(fixture.DirectoryPath, "repaired.flv"), new InlineProgress<double>(_ => { }), CancellationToken.None);
        Assert.NotEmpty(result.Outputs);
        Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        foreach (var output in result.Outputs)
        {
            Assert.True(File.Exists(output.Path));
            Assert.True(File.Exists(output.IndexPath));
            Assert.InRange((await fixture.Processor.ProbeAsync(output.Path)).DurationMs, 3800, 4300);
        }
    }
}
