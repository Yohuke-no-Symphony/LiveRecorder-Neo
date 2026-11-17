using System.Buffers;
using System.Buffers.Binary;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Plans committed byte ranges and reconstructs independently playable FLV inputs.</summary>
public static class IndexedFlvRange
{
    private const int TAG_HEADER_BYTES = 11;
    private const int PREVIOUS_TAG_SIZE_BYTES = 4;
    private const int MAX_TAG_PAYLOAD_BYTES = 0xFFFFFF;

    /// <summary>Plans the keyframe-aligned bytes and decoder headers within a fixed source snapshot.</summary>
    public static async Task<IndexedFlvRangePlan> PlanAsync(string indexPath, long committedBytes, double startMs,
        double durationMs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(committedBytes);
        if (!double.IsFinite(startMs) || startMs < 0 || !double.IsFinite(durationMs) || durationMs <= 0
            || !double.IsFinite(startMs + durationMs))
        {
            throw new ArgumentOutOfRangeException(nameof(startMs), "An indexed range needs finite, positive requested bounds.");
        }
        var index = await FlvIndexSnapshot.ReadAsync(indexPath, committedBytes, cancellationToken);
        var committed = index.Entries.LastOrDefault(entry => entry.Kind == "Committed");
        if (committed is null || committed.Offset <= 0 || !double.IsFinite(committed.TimestampMs) || committed.TimestampMs <= 0
            || startMs >= committed.TimestampMs || startMs + durationMs > committed.TimestampMs + 150)
        {
            throw new InvalidOperationException("The selected export range has not been committed yet.");
        }
        var keyframes = index.Keyframes.Where(frame => frame.Offset >= 0 && frame.Offset < committed.Offset
            && frame.Offset + TAG_HEADER_BYTES + frame.Size + PREVIOUS_TAG_SIZE_BYTES <= committed.Offset).ToArray();
        var first = keyframes.LastOrDefault(frame => frame.TimestampMs <= startMs) ?? keyframes.FirstOrDefault()
            ?? throw new InvalidOperationException("The selected export range has no committed keyframe.");
        var after = keyframes.FirstOrDefault(frame => frame.TimestampMs >= startMs + durationMs && frame.Offset > first.Offset);
        var plan = new IndexedFlvRangePlan
        {
            SourceLength = committedBytes,
            StartOffset = first.Offset,
            EndOffset = after?.Offset ?? committed.Offset,
            StartMs = first.TimestampMs,
            EndMs = after?.TimestampMs ?? committed.TimestampMs,
            VideoHeader = index.Entries.LastOrDefault(entry => entry.Kind == "VideoHeader" && entry.Offset < first.Offset)?.HeaderData?.ToArray()
                ?? throw new InvalidDataException("No indexed AVC/HEVC decoder configuration is available for this window."),
            AudioHeader = index.Entries.LastOrDefault(entry => entry.Kind == "AudioHeader" && entry.Offset < first.Offset)?.HeaderData?.ToArray()
        };
        ValidatePlan(plan);
        return plan;
    }

    /// <summary>Rebuilds a timestamp-normalized FLV from exactly the supplied range without seeking or closing either stream.</summary>
    public static Task CopyAsync(Stream input, IndexedFlvRangePlan plan, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        ValidatePlan(plan);
        return CopyTagsAsync(input, plan.Length, plan.StartMs, plan.VideoHeader, plan.AudioHeader, output, cancellationToken);
    }

    /// <summary>Plans the normalized media bytes produced by reconstructing an indexed range, excluding its repeated decoder headers.</summary>
    public static IndexedFlvRangePlan PlanReconstructedInput(IndexedFlvRangePlan sourcePlan)
    {
        ArgumentNullException.ThrowIfNull(sourcePlan);
        ValidatePlan(sourcePlan);
        var prefixBytes = 13 + TAG_HEADER_BYTES + PREVIOUS_TAG_SIZE_BYTES + sourcePlan.VideoHeader.Length
            + (sourcePlan.AudioHeader is { } audio ? TAG_HEADER_BYTES + PREVIOUS_TAG_SIZE_BYTES + audio.Length : 0);
        var length = checked(prefixBytes + sourcePlan.Length);
        return new()
        {
            SourceLength = length,
            StartOffset = prefixBytes,
            EndOffset = length,
            StartMs = 0,
            EndMs = sourcePlan.EndMs - sourcePlan.StartMs,
            VideoHeader = sourcePlan.VideoHeader.ToArray(),
            AudioHeader = sourcePlan.AudioHeader?.ToArray()
        };
    }

    internal static async Task CopyAsync(string path, IndexedFlvRangePlan plan, Stream output, CancellationToken cancellationToken)
    {
        ValidatePlan(plan);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        source.Position = plan.StartOffset;
        await CopyAsync(source, plan, output, cancellationToken);
    }

    internal static async Task AppendAsync(string path, IndexedFlvRangePlan plan, Stream output, double outputStartMs,
        bool includeHeaders, CancellationToken cancellationToken)
    {
        ValidatePlan(plan);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        source.Position = plan.StartOffset;
        await CopyTagsAsync(source, plan.Length, plan.StartMs, plan.VideoHeader, plan.AudioHeader, output, cancellationToken,
            outputStartMs, includeHeaders, preserveTimeline: true);
    }

    internal static async Task CopyAsync(string path, FlvIndexSnapshot index, FlvIndexEntry first, long endOffset, Stream output, CancellationToken cancellationToken)
    {
        var video = index.Entries.LastOrDefault(entry => entry.Kind == "VideoHeader" && entry.Offset < first.Offset);
        var audio = index.Entries.LastOrDefault(entry => entry.Kind == "AudioHeader" && entry.Offset < first.Offset);
        if (video?.HeaderData is null)
        {
            throw new InvalidDataException("No indexed AVC/HEVC decoder configuration is available for this window.");
        }

        await CopyAsync(path, first, endOffset, first.TimestampMs, video.HeaderData, audio?.HeaderData, output, cancellationToken);
    }

    internal static async Task CopyAsync(string path, FlvIndexEntry first, long endOffset, double timestampOriginMs, byte[] videoHeader, byte[]? audioHeader,
        Stream output, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        if (first.Offset < 0 || endOffset <= first.Offset || endOffset > source.Length)
        {
            throw new InvalidDataException("The indexed byte range exceeds the available source.");
        }
        source.Position = first.Offset;
        await CopyTagsAsync(source, endOffset - first.Offset, timestampOriginMs, videoHeader, audioHeader, output, cancellationToken);
    }

    private static async Task CopyTagsAsync(Stream source, long length, double timestampOriginMs, byte[] videoHeader, byte[]? audioHeader,
        Stream output, CancellationToken cancellationToken, double outputStartMs = 0, bool includeHeaders = true, bool preserveTimeline = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDecoderHeaders(videoHeader, audioHeader);
        if (length < TAG_HEADER_BYTES + PREVIOUS_TAG_SIZE_BYTES || !double.IsFinite(timestampOriginMs)
            || timestampOriginMs < 0 || timestampOriginMs > uint.MaxValue || !double.IsFinite(outputStartMs)
            || outputStartMs < 0 || outputStartMs > uint.MaxValue)
        {
            throw new InvalidDataException("The indexed byte range or timestamp origin is invalid.");
        }
        if (includeHeaders)
        {
            await output.WriteAsync(new byte[] { 70, 76, 86, 1, (byte)(audioHeader is null ? 1 : 5), 0, 0, 0, 9, 0, 0, 0, 0 }, cancellationToken);
            await WriteHeaderAsync(output, 9, videoHeader, cancellationToken);
            if (audioHeader is { } audioData)
            {
                await WriteHeaderAsync(output, 8, audioData, cancellationToken);
            }
        }

        var header = new byte[TAG_HEADER_BYTES];
        var previousSize = new byte[PREVIOUS_TAG_SIZE_BYTES];
        var rented = ArrayPool<byte>.Shared.Rent(65536);
        var remainingBytes = length;
        try
        {
            while (remainingBytes > 0)
            {
                if (remainingBytes < TAG_HEADER_BYTES + PREVIOUS_TAG_SIZE_BYTES)
                {
                    throw new InvalidDataException("The committed window ends inside an FLV tag.");
                }
                await source.ReadExactlyAsync(header, cancellationToken);
                remainingBytes -= TAG_HEADER_BYTES;
                var size = (header[1] << 16) | (header[2] << 8) | header[3];
                if (size + PREVIOUS_TAG_SIZE_BYTES > remainingBytes)
                {
                    throw new InvalidDataException("The committed window ends inside an FLV tag.");
                }
                var timestamp = ((uint)header[7] << 24) | ((uint)header[4] << 16) | ((uint)header[5] << 8) | header[6];
                var relativeMs = timestamp - (long)timestampOriginMs;
                relativeMs = (preserveTimeline ? relativeMs : Math.Max(0, relativeMs)) + (long)Math.Round(outputStartMs);
                if (relativeMs is < 0 or > uint.MaxValue)
                {
                    throw new InvalidDataException("The continuous FLV input exceeds its timestamp range.");
                }
                var relative = (uint)relativeMs;
                header[4] = (byte)(relative >> 16);
                header[5] = (byte)(relative >> 8);
                header[6] = (byte)relative;
                header[7] = (byte)(relative >> 24);
                await output.WriteAsync(header, cancellationToken);
                var remaining = size;
                while (remaining > 0)
                {
                    var count = Math.Min(rented.Length, remaining);
                    await source.ReadExactlyAsync(rented.AsMemory(0, count), cancellationToken);
                    await output.WriteAsync(rented.AsMemory(0, count), cancellationToken);
                    remaining -= count;
                    remainingBytes -= count;
                }
                await source.ReadExactlyAsync(previousSize, cancellationToken);
                remainingBytes -= PREVIOUS_TAG_SIZE_BYTES;
                if (BinaryPrimitives.ReadInt32BigEndian(previousSize) != TAG_HEADER_BYTES + size)
                {
                    throw new InvalidDataException("The indexed FLV tag has an invalid previous-tag size.");
                }
                await output.WriteAsync(previousSize, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ValidatePlan(IndexedFlvRangePlan plan)
    {
        if (plan.SourceLength <= 0 || plan.StartOffset < 0 || plan.EndOffset <= plan.StartOffset
            || plan.EndOffset > plan.SourceLength || plan.Length < TAG_HEADER_BYTES + PREVIOUS_TAG_SIZE_BYTES
            || !double.IsFinite(plan.StartMs) || plan.StartMs < 0 || plan.StartMs > uint.MaxValue
            || !double.IsFinite(plan.EndMs) || plan.EndMs <= plan.StartMs)
        {
            throw new ArgumentException("An indexed range must stay within its source snapshot and have valid timeline bounds.", nameof(plan));
        }
        ValidateDecoderHeaders(plan.VideoHeader, plan.AudioHeader);
    }

    private static void ValidateDecoderHeaders(byte[] videoHeader, byte[]? audioHeader)
    {
        if (videoHeader is not { Length: > 0 and <= MAX_TAG_PAYLOAD_BYTES }
            || audioHeader is { Length: 0 or > MAX_TAG_PAYLOAD_BYTES })
        {
            throw new InvalidDataException("The indexed decoder configuration is invalid.");
        }
    }

    private static async Task WriteHeaderAsync(Stream output, byte type, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[11];
        header[0] = type;
        header[1] = (byte)(payload.Length >> 16);
        header[2] = (byte)(payload.Length >> 8);
        header[3] = (byte)payload.Length;
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        var previousSize = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(previousSize, 11 + payload.Length);
        await output.WriteAsync(previousSize, cancellationToken);
    }
}
