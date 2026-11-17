using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Verifies portable FLV ranges without downloading unrelated source bytes.</summary>
public sealed class IndexedFlvRangeTests
{
    /// <summary>Plans a bounded keyframe range with the decoder headers active at its start.</summary>
    [Fact]
    public async Task PlansOnlyRequiredBytesAndLatestDecoderHeaders()
    {
        using var fixture = new MediaFixture();
        var entries = CreateIndex();
        entries.Insert(3, new() { Kind = "VideoHeader", Offset = 200, HeaderData = [0x17, 0, 2] });
        entries.Insert(5, new() { Kind = "VideoHeader", Offset = 400, HeaderData = [0x17, 0, 3] });
        var path = await WriteIndexAsync(fixture, entries);

        var plan = await IndexedFlvRange.PlanAsync(path, 1000, 1250, 500, CancellationToken.None);

        Assert.Equal(1000, plan.SourceLength);
        Assert.Equal(300, plan.StartOffset);
        Assert.Equal(500, plan.EndOffset);
        Assert.Equal(200, plan.Length);
        Assert.Equal(1000, plan.StartMs);
        Assert.Equal(2000, plan.EndMs);
        Assert.Equal(new byte[] { 0x17, 0, 2 }, plan.VideoHeader);
        Assert.Equal(new byte[] { 0xaf, 0, 1 }, plan.AudioHeader);
    }

    /// <summary>Appended index entries cannot extend a plan past its committed source snapshot.</summary>
    [Fact]
    public async Task GrowingIndexRemainsBoundedByCommittedSnapshot()
    {
        using var fixture = new MediaFixture();
        var entries = CreateIndex();
        entries.Add(new() { Kind = "Keyframe", Offset = 1100, TimestampMs = 4000 });
        entries.Add(new() { Kind = "Committed", Offset = 1400, TimestampMs = 5000 });
        var path = await WriteIndexAsync(fixture, entries);

        var plan = await IndexedFlvRange.PlanAsync(path, 1000, 3300, 500, CancellationToken.None);

        Assert.Equal(700, plan.StartOffset);
        Assert.Equal(1000, plan.EndOffset);
        Assert.Equal(4000, plan.EndMs);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndexedFlvRange.PlanAsync(path, 1000, 4300, 500, CancellationToken.None));
    }

    /// <summary>Uncommitted keyframes cannot supply the end boundary of a committed range.</summary>
    [Fact]
    public async Task KeyframesAfterLastCommitDoNotExtendRange()
    {
        using var fixture = new MediaFixture();
        var entries = CreateIndex();
        entries.Add(new() { Kind = "Keyframe", Offset = 1100, TimestampMs = 4000 });
        var path = await WriteIndexAsync(fixture, entries);

        var plan = await IndexedFlvRange.PlanAsync(path, 1400, 3300, 500, CancellationToken.None);

        Assert.Equal(1000, plan.EndOffset);
        Assert.Equal(4000, plan.EndMs);
    }

    /// <summary>Invalid requested times fail before any index file is opened.</summary>
    [Theory]
    [InlineData(-1, 500)]
    [InlineData(double.NaN, 500)]
    [InlineData(double.PositiveInfinity, 500)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public async Task RejectsInvalidRequestedTimes(double startMs, double durationMs)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            IndexedFlvRange.PlanAsync("missing.index.jsonl", 1000, startMs, durationMs, CancellationToken.None));
    }

    /// <summary>A keyframe without its decoder configuration cannot become a portable input.</summary>
    [Fact]
    public async Task RejectsMissingVideoDecoderHeader()
    {
        using var fixture = new MediaFixture();
        var entries = CreateIndex();
        entries.RemoveAll(entry => entry.Kind == "VideoHeader");
        var path = await WriteIndexAsync(fixture, entries);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexedFlvRange.PlanAsync(path, 1000, 1250, 500, CancellationToken.None));
    }

    /// <summary>Reconstruction supports a non-seekable input and consumes exactly the planned bytes.</summary>
    [Fact]
    public async Task ReconstructsOnlyPlannedBytesFromNonSeekableStream()
    {
        var raw = CreateTag(9, 1500, [0x17, 1, 0, 0, 0, 0x65])
            .Concat(CreateTag(8, 1520, [0xaf, 1, 2])).ToArray();
        var trailing = new byte[] { 11, 22, 33 };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(raw.Concat(trailing).ToArray());
        await pipe.Writer.CompleteAsync();
        await using var input = pipe.Reader.AsStream();
        await using var output = new MemoryStream();
        var plan = CreatePlan(raw.Length, 1500);

        Assert.False(input.CanSeek);
        await IndexedFlvRange.CopyAsync(input, plan, output, CancellationToken.None);

        var remaining = new byte[trailing.Length];
        await input.ReadExactlyAsync(remaining);
        Assert.Equal(trailing, remaining);
        var bytes = output.ToArray();
        Assert.Equal(new byte[] { 70, 76, 86, 1, 5, 0, 0, 0, 9, 0, 0, 0, 0 }, bytes[..13]);
        var rawStart = 13 + 15 + plan.VideoHeader.Length + 15 + plan.AudioHeader!.Length;
        Assert.Equal(0u, ReadTimestamp(bytes.AsSpan(rawStart, 11)));
        Assert.Equal(20u, ReadTimestamp(bytes.AsSpan(rawStart + 21, 11)));
        Assert.Equal(raw.Length + rawStart, bytes.Length);
    }

    /// <summary>FLV timestamps above the signed integer limit retain their relative positions.</summary>
    [Fact]
    public async Task NormalizesUnsignedFlvTimestamps()
    {
        var raw = CreateTag(9, 3_000_000_000, [0x17, 1, 0, 0, 0, 0x65]);
        var plan = CreatePlan(raw.Length, 2_999_999_000);
        await using var input = new MemoryStream(raw);
        await using var output = new MemoryStream();

        await IndexedFlvRange.CopyAsync(input, plan, output, CancellationToken.None);

        var rawStart = 13 + 15 + plan.VideoHeader.Length + 15 + plan.AudioHeader!.Length;
        Assert.Equal(1000u, ReadTimestamp(output.ToArray().AsSpan(rawStart, 11)));
    }

    /// <summary>A tag cannot cause reconstruction to read past the assigned range.</summary>
    [Fact]
    public async Task RejectsRangeEndingInsideTagWithoutReadingPastBoundary()
    {
        var raw = CreateTag(9, 1500, [0x17, 1, 0, 0, 0, 0x65]);
        await using var input = new MemoryStream(raw);
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexedFlvRange.CopyAsync(input, CreatePlan(raw.Length - 1, 1500), output, CancellationToken.None));

        Assert.Equal(11, input.Position);
    }

    /// <summary>Truncated transferred bytes are rejected rather than producing a successful input.</summary>
    [Fact]
    public async Task RejectsTruncatedTransferredRange()
    {
        var raw = CreateTag(9, 1500, [0x17, 1, 0, 0, 0, 0x65]);
        await using var input = new MemoryStream(raw[..^1]);
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            IndexedFlvRange.CopyAsync(input, CreatePlan(raw.Length, 1500), output, CancellationToken.None));
    }

    /// <summary>A corrupted previous-tag size cannot produce a successful reconstructed input.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public async Task RejectsCorruptedPreviousTagSize(int previousTagSize)
    {
        var raw = CreateTag(9, 1500, [0x17, 1, 0, 0, 0, 0x65]);
        BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(raw.Length - 4), previousTagSize);
        await using var input = new MemoryStream(raw);
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexedFlvRange.CopyAsync(input, CreatePlan(raw.Length, 1500), output, CancellationToken.None));
    }

    /// <summary>Invalid byte boundaries cannot access source bytes outside the snapshot.</summary>
    [Theory]
    [InlineData(-1, 20, 100)]
    [InlineData(10, 10, 100)]
    [InlineData(20, 10, 100)]
    [InlineData(90, 110, 100)]
    [InlineData(0, 20, 0)]
    [InlineData(0, long.MaxValue, 100)]
    public async Task RejectsInvalidByteBoundaries(long startOffset, long endOffset, long sourceLength)
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        var plan = CreatePlan(20, 0) with { StartOffset = startOffset, EndOffset = endOffset, SourceLength = sourceLength };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            IndexedFlvRange.CopyAsync(input, plan, output, CancellationToken.None));

        Assert.Equal(0, output.Length);
    }

    /// <summary>A pre-cancelled reconstruction never emits a temporary FLV header.</summary>
    [Fact]
    public async Task CancellationDoesNotWriteOutput()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IndexedFlvRange.CopyAsync(input, CreatePlan(20, 0), output, cancellation.Token));

        Assert.Equal(0, output.Length);
    }

    /// <summary>Real media reconstructed from a small middle range is independently playable.</summary>
    [Fact]
    public async Task ReconstructedMiddleRangeIsPlayableWithoutOriginalFile()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 8);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var sourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath);
        var plan = await IndexedFlvRange.PlanAsync(Path.ChangeExtension(sourcePath, ".index.jsonl"),
            new FileInfo(sourcePath).Length, 3500, 750, CancellationToken.None);
        var raw = new byte[checked((int)plan.Length)];
        await using (var source = File.OpenRead(sourcePath))
        {
            source.Position = plan.StartOffset;
            await source.ReadExactlyAsync(raw);
        }
        Assert.True(raw.Length < plan.SourceLength / 2);
        var rebuiltPath = Path.Combine(fixture.DirectoryPath, "range-only.flv");
        await using (var input = new MemoryStream(raw))
        await using (var output = File.Create(rebuiltPath))
        {
            await IndexedFlvRange.CopyAsync(input, plan, output, CancellationToken.None);
        }
        File.Delete(sourcePath);

        var probe = await fixture.Processor.ProbeAsync(rebuiltPath);

        Assert.Equal("h264", probe.VideoCodec);
        Assert.True(probe.HasAudio);
        Assert.InRange(probe.DurationMs, plan.EndMs - plan.StartMs - 150, plan.EndMs - plan.StartMs + 150);
        await fixture.Processes.RunAsync(MediaProcessKind.Export,
            ["-v", "error", "-xerror", "-i", rebuiltPath, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"], CancellationToken.None);
    }

    /// <summary>Appending a later range preserves audio that precedes its video keyframe in decode time.</summary>
    [Fact]
    public async Task ContinuousAppendPreservesInterleavedAudioBeforeTheRangeOrigin()
    {
        using var fixture = new MediaFixture();
        var video = CreateTag(9, 2000, [0x17, 1, 0, 0, 0, 0x65]);
        var raw = video.Concat(CreateTag(8, 1990, [0xaf, 1, 2])).ToArray();
        var path = Path.Combine(fixture.DirectoryPath, "interleaved.range");
        await File.WriteAllBytesAsync(path, raw);
        var plan = CreatePlan(raw.Length, 2000) with { StartOffset = 0, EndOffset = raw.Length, SourceLength = raw.Length };
        await using var output = new MemoryStream();

        await IndexedFlvRange.AppendAsync(path, plan, output, 1000, false, CancellationToken.None);

        var bytes = output.ToArray();
        Assert.Equal(raw.Length, bytes.Length);
        Assert.Equal(1000u, ReadTimestamp(bytes.AsSpan(0, 11)));
        Assert.Equal(990u, ReadTimestamp(bytes.AsSpan(video.Length, 11)));
    }

    /// <summary>A continuous input fails instead of silently clamping media before its common timestamp origin.</summary>
    [Fact]
    public async Task ContinuousAppendRejectsAnUnrepresentableNegativeTimestamp()
    {
        using var fixture = new MediaFixture();
        var raw = CreateTag(8, 1990, [0xaf, 1, 2]);
        var path = Path.Combine(fixture.DirectoryPath, "negative.range");
        await File.WriteAllBytesAsync(path, raw);
        var plan = CreatePlan(raw.Length, 2000) with { StartOffset = 0, EndOffset = raw.Length, SourceLength = raw.Length };
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IndexedFlvRange.AppendAsync(path, plan, output, 0, false, CancellationToken.None));

        Assert.Equal(0, output.Length);
    }

    private static List<FlvIndexEntry> CreateIndex()
    {
        return
        [
            new() { Kind = "VideoHeader", Offset = 0, HeaderData = [0x17, 0, 1] },
            new() { Kind = "AudioHeader", Offset = 40, HeaderData = [0xaf, 0, 1] },
            new() { Kind = "Keyframe", Offset = 100, TimestampMs = 0 },
            new() { Kind = "Keyframe", Offset = 300, TimestampMs = 1000 },
            new() { Kind = "Keyframe", Offset = 500, TimestampMs = 2000 },
            new() { Kind = "Keyframe", Offset = 700, TimestampMs = 3000 },
            new() { Kind = "Committed", Offset = 1000, TimestampMs = 4000 }
        ];
    }

    private static async Task<string> WriteIndexAsync(MediaFixture fixture, IEnumerable<FlvIndexEntry> entries)
    {
        var path = Path.Combine(fixture.DirectoryPath, "range.index.jsonl");
        await File.WriteAllLinesAsync(path, entries.Select(entry => JsonSerializer.Serialize(entry, MediaJson.Options)));
        return path;
    }

    private static IndexedFlvRangePlan CreatePlan(int length, double startMs)
    {
        return new()
        {
            SourceLength = 10000,
            StartOffset = 300,
            EndOffset = 300 + length,
            StartMs = startMs,
            EndMs = startMs + 1000,
            VideoHeader = [0x17, 0, 1],
            AudioHeader = [0xaf, 0, 1]
        };
    }

    private static byte[] CreateTag(byte type, uint timestamp, byte[] payload)
    {
        var tag = new byte[15 + payload.Length];
        tag[0] = type;
        tag[1] = (byte)(payload.Length >> 16);
        tag[2] = (byte)(payload.Length >> 8);
        tag[3] = (byte)payload.Length;
        tag[4] = (byte)(timestamp >> 16);
        tag[5] = (byte)(timestamp >> 8);
        tag[6] = (byte)timestamp;
        tag[7] = (byte)(timestamp >> 24);
        payload.CopyTo(tag, 11);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(11 + payload.Length), 11 + payload.Length);
        return tag;
    }

    private static uint ReadTimestamp(ReadOnlySpan<byte> header)
    {
        return ((uint)header[7] << 24) | ((uint)header[4] << 16) | ((uint)header[5] << 8) | header[6];
    }
}
